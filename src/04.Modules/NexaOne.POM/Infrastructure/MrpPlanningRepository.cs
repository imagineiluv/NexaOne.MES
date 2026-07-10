using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Mrp;
using NexaOne.POM.Domain.Mrp;
using NexaOne.ServiceContracts.Pom;

namespace NexaOne.POM.Infrastructure;

/// <summary>MRP v1 실행 구현(POM 모듈 소유) — 원자료(SLS 수요·PRC/POM 예정입고·MDM BOM/계획파라미터/
/// 벤더품목·IVT 재고)를 읽어 <see cref="MrpCalculator"/>(순수)로 넷팅하고 MRP_RUN/MRP_PLANNED_ORDER에
/// append-only 영속한다. DB 접근은 모듈 표준(QueryRepository 읽기 + ServiceObjectProcessor 쓰기,
/// provider-agnostic Dapper — SQLite/MSSQL 공통 ANSI SQL). 교차모듈 데이터는 C# 타입 결합 없이 순수
/// SQL로만 읽는다(OEE 집계 리포 선례). ⚠ V080 ALTER 컬럼(MDM_BOM.SCRAP_RATE·PRC.PRODUCT_ID)을
/// 참조하므로 V080 미적용 DB(구 dev)는 재생성이 필요하다(스펙/런북 문서화).</summary>
public sealed class MrpPlanningRepository : QueryRepository, IMrpPlanner
{
    private const string Ts = "yyyy-MM-dd HH:mm:ss";
    private readonly ServiceObjectProcessor _processor;

    public MrpPlanningRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<MrpRunResult> RunAsync(string executedBy, CancellationToken ct = default)
    {
        var runId = $"MRP_{DateTime.UtcNow:yyyyMMddHHmmss}_{Guid.NewGuid():N}"[..40];
        var startedAt = DateTime.UtcNow.ToString(Ts);
        await _processor.ExecuteAsync(
            "INSERT INTO MRP_RUN (RUN_ID, STARTED_AT, STATUS, EXECUTED_BY, CREATED_BY, UPDATED_BY) " +
            "VALUES (@runId, @startedAt, 'Running', @by, @by, @by)",
            new { runId, startedAt, by = executedBy }, ct);

        try
        {
            var demands = await LoadDemandsAsync(ct);
            var result = MrpCalculator.Calculate(
                demands,
                await LoadBomAsync(ct),
                await LoadOnHandAsync(ct),
                await LoadOnOrderAsync(ct),
                await LoadPlanningAsync(ct),
                await LoadVendorsAsync(ct));

            if (!result.Success)
            {
                await FinalizeAsync(runId, "Failed", demands.Count, 0, result.Error, executedBy, ct);
                return new MrpRunResult(runId, "Failed", demands.Count, 0, result.Error);
            }

            var seq = 0;
            foreach (var p in result.Proposals)
            {
                await _processor.ExecuteAsync(
                    "INSERT INTO MRP_PLANNED_ORDER (PLANNED_ORDER_ID, RUN_ID, ITEM_ID, ORDER_TYPE, GROSS_QTY, " +
                    "ON_HAND_QTY, ON_ORDER_QTY, SAFETY_STOCK_QTY, NET_QTY, SUGGESTED_QTY, DUE_DATE, RELEASE_DATE, " +
                    "SOURCE_DEMAND, PLANT_ID, CREATED_BY, UPDATED_BY) " +
                    "VALUES (@id, @runId, @item, @type, @gross, @onHand, @onOrder, @safety, @net, @suggested, " +
                    "@due, @release, @source, @plant, @by, @by)",
                    new
                    {
                        id = $"{runId}_{++seq:D4}",
                        runId,
                        item = p.ItemId,
                        type = p.OrderType,
                        gross = p.GrossQty,
                        onHand = p.OnHandQty,
                        onOrder = p.OnOrderQty,
                        safety = p.SafetyStockQty,
                        net = p.NetQty,
                        suggested = p.SuggestedQty,
                        due = p.DueDate?.ToString(Ts),
                        release = p.ReleaseDate?.ToString(Ts),
                        source = p.SourceDemand,
                        plant = p.PlantId,
                        by = executedBy,
                    }, ct);

                // 페깅(v2) — 총소요의 수요별 기여를 분해 보존(SOURCE_DEMAND 요약의 정밀판). append-only.
                var pseq = 0;
                foreach (var cb in p.Contributions ?? Array.Empty<MrpContribution>())
                    await _processor.ExecuteAsync(
                        "INSERT INTO MRP_PEGGING (PEGGING_ID, RUN_ID, PLANNED_ORDER_ID, ITEM_ID, DEMAND_REF, QTY, CREATED_BY) " +
                        "VALUES (@id, @runId, @orderId, @item, @demandRef, @qty, @by)",
                        new
                        {
                            id = $"{runId}_{seq:D4}_P{++pseq:D3}",
                            runId,
                            orderId = $"{runId}_{seq:D4}",
                            item = p.ItemId,
                            demandRef = cb.DemandRef,
                            qty = cb.Qty,
                            by = executedBy,
                        }, ct);
            }

            await FinalizeAsync(runId, "Success", demands.Count, result.Proposals.Count, null, executedBy, ct);
            return new MrpRunResult(runId, "Success", demands.Count, result.Proposals.Count, null);
        }
        catch (Exception ex)
        {
            // 적재/계산 예외 — 런을 Failed로 마감해 이력에 사유를 남긴다(원자료 무변경이라 재실행 안전).
            var message = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
            await FinalizeAsync(runId, "Failed", 0, 0, message, executedBy, ct);
            return new MrpRunResult(runId, "Failed", 0, 0, message);
        }
    }

    /// <summary>Proposed 제안→실오더 전환(v2 1단). 정책: Purchase→PRC 구매오더 'Ordered' 직행,
    /// Production→POM 작업지시 'Released' 직행 — Draft/Created는 예정입고 집계(Ordered|Incoming /
    /// Released|Started) 밖이라 전환 직후 MRP 재실행 시 같은 수요가 이중 제안되기 때문(설계 결정).
    /// 원자성: 실오더 INSERT + 제안 Converted 마킹 전 문장을 단일 ExecuteManyAsync 트랜잭션으로 커밋
    /// (MixingPersistAsync/DATA-3 패턴 — 부분 커밋 불가). ⚠ ExecuteManyAsync는 감사컬럼 자동주입이
    /// 없어 CREATED_BY/UPDATED_BY를 문장에 명시한다(시각은 DDL DEFAULT).</summary>
    public async Task<MrpConvertResult> ConvertAsync(string? runId, string executedBy, CancellationToken ct = default)
    {
        try
        {
            runId ??= (await QueryAsync<string>(
                "SELECT RUN_ID FROM MRP_RUN WHERE STARTED_AT = (SELECT MAX(STARTED_AT) FROM MRP_RUN)", null, ct))
                .FirstOrDefault();
            if (runId is null)
                return new MrpConvertResult("", 0, 0, 0, "실행 이력이 없습니다 — 먼저 MRP를 실행하세요.");

            var rows = (await QueryAsync<dynamic>(
                "SELECT PLANNED_ORDER_ID, ITEM_ID, ORDER_TYPE, SUGGESTED_QTY, DUE_DATE, RELEASE_DATE, PLANT_ID " +
                "FROM MRP_PLANNED_ORDER WHERE RUN_ID = @runId AND STATUS = 'Proposed' ORDER BY PLANNED_ORDER_ID",
                new { runId }, ct)).ToList();
            if (rows.Count == 0)
                return new MrpConvertResult(runId, 0, 0, 0, "전환 대상(Proposed)이 없습니다.");

            const string MarkSql =
                "UPDATE MRP_PLANNED_ORDER SET STATUS = 'Converted', CONVERTED_ORDER_ID = @newId, " +
                "UPDATED_BY = @by, UPDATED_AT = @now WHERE PLANNED_ORDER_ID = @id AND STATUS = 'Proposed'";
            var now = DateTime.UtcNow.ToString(Ts);
            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            var statements = new List<(string Sql, object? Param)>();
            int po = 0, wo = 0, seq = 0;

            foreach (var r in rows)
            {
                seq++;
                var plannedId = (string)r.PLANNED_ORDER_ID;
                var item = (string)r.ITEM_ID;
                var qty = Convert.ToDecimal(r.SUGGESTED_QTY);
                var due = AsDate(r.DUE_DATE)?.ToString(Ts);
                var release = AsDate(r.RELEASE_DATE)?.ToString(Ts);
                var plant = (r.PLANT_ID as string) ?? "DEFAULT";   // 공장 미상(구 런/직접입력 수요) 폴백

                if (string.Equals((string)r.ORDER_TYPE, "Purchase", StringComparison.OrdinalIgnoreCase))
                {
                    var orderId = $"PO-MRP-{stamp}-{seq:D3}";
                    po++;
                    statements.Add((
                        "INSERT INTO PRC_PURCHASE_ORDER (PURCHASE_ORDER_ID, PLANT_ID, PURCHASE_ORDER_NAME, " +
                        "ORDER_DATE, INCOMING_DATE, ORDER_QTY, PRODUCT_ID, STATUS, DESCRIPTION, CREATED_BY, UPDATED_BY) " +
                        "VALUES (@id, @plant, @name, @orderDate, @incoming, @qty, @item, 'Ordered', @desc, @by, @by)",
                        new
                        {
                            id = orderId, plant, name = $"MRP 전환 — {item}", orderDate = now, incoming = due,
                            qty, item, desc = $"MRP {runId} / {plannedId}", by = executedBy,
                        }));
                    statements.Add((MarkSql, new { newId = orderId, id = plannedId, by = executedBy, now }));
                }
                else
                {
                    var orderId = $"WO-MRP-{stamp}-{seq:D3}";
                    wo++;
                    statements.Add((
                        "INSERT INTO POM_WORK_ORDER (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCT_ID, " +
                        "PLAN_START_DATE, PLAN_END_DATE, PLAN_QTY, STATUS, DESCRIPTION, CREATED_BY, UPDATED_BY) " +
                        "VALUES (@id, @plant, @name, @item, @planStart, @planEnd, @qty, 'Released', @desc, @by, @by)",
                        new
                        {
                            id = orderId, plant, name = $"MRP 전환 — {item}", item,
                            planStart = release ?? now, planEnd = due, qty,
                            desc = $"MRP {runId} / {plannedId}", by = executedBy,
                        }));
                    statements.Add((MarkSql, new { newId = orderId, id = plannedId, by = executedBy, now }));
                }
            }

            await _processor.ExecuteManyAsync(ct, statements.ToArray());
            return new MrpConvertResult(runId, po + wo, po, wo, null);
        }
        catch (Exception ex)
        {
            var message = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
            return new MrpConvertResult(runId ?? "", 0, 0, 0, message);
        }
    }

    private Task FinalizeAsync(
        string runId, string status, int demandCount, int orderCount, string? message, string by, CancellationToken ct)
        => _processor.ExecuteAsync(
            "UPDATE MRP_RUN SET STATUS = @status, FINISHED_AT = @finishedAt, DEMAND_COUNT = @demandCount, " +
            "PLANNED_ORDER_COUNT = @orderCount, MESSAGE = @message, UPDATED_BY = @by WHERE RUN_ID = @runId",
            new { runId, status, finishedAt = DateTime.UtcNow.ToString(Ts), demandCount, orderCount, message, by }, ct);

    // ── 원자료 적재(순수 SQL — 교차모듈 read, ADR-001) ─────────────────────────

    private async Task<IReadOnlyList<MrpDemand>> LoadDemandsAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            "SELECT SALES_ORDER_ID, PRODUCT_ID, (PLAN_QTY - COALESCE(DELIVERED_QTY, 0)) AS OPEN_QTY, PLAN_END_DATE, PLANT_ID " +
            "FROM SLS_SALES_ORDER " +
            "WHERE STATUS IN ('Confirmed', 'Producing') AND PRODUCT_ID IS NOT NULL " +
            "  AND (PLAN_QTY - COALESCE(DELIVERED_QTY, 0)) > 0", null, ct);
        return rows.Select(r => new MrpDemand(
            (string)r.PRODUCT_ID, Convert.ToDecimal(r.OPEN_QTY), AsDate(r.PLAN_END_DATE), (string)r.SALES_ORDER_ID,
            r.PLANT_ID as string)).ToList();
    }

    private async Task<IReadOnlyList<MrpBomLine>> LoadBomAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            "SELECT PRODUCT_ID, COMPONENT_ID, QUANTITY, COALESCE(SCRAP_RATE, 0) AS SCRAP_RATE FROM MDM_BOM", null, ct);
        return rows.Select(r => new MrpBomLine(
            (string)r.PRODUCT_ID, (string)r.COMPONENT_ID, Convert.ToDecimal(r.QUANTITY), Convert.ToDecimal(r.SCRAP_RATE))).ToList();
    }

    private async Task<IReadOnlyDictionary<string, decimal>> LoadOnHandAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            "SELECT MATERIAL_ID, SUM(CURRENT_QTY) AS QTY FROM IVT_MATERIAL_LOT GROUP BY MATERIAL_ID", null, ct);
        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var r in rows) map[(string)r.MATERIAL_ID] = Convert.ToDecimal(r.QTY);
        return map;
    }

    private async Task<IReadOnlyDictionary<string, decimal>> LoadOnOrderAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, decimal>(StringComparer.Ordinal);
        // 예정입고 ①구매(V080 PRODUCT_ID 링크가 있는 행만 — 표준 갭 보수분) ②생산(작업지시 미완수량).
        var po = await QueryAsync<dynamic>(
            "SELECT PRODUCT_ID, SUM(ORDER_QTY) AS QTY FROM PRC_PURCHASE_ORDER " +
            "WHERE STATUS IN ('Ordered', 'Incoming') AND PRODUCT_ID IS NOT NULL GROUP BY PRODUCT_ID", null, ct);
        foreach (var r in po) map[(string)r.PRODUCT_ID] = map.GetValueOrDefault((string)r.PRODUCT_ID) + Convert.ToDecimal(r.QTY);
        var wo = await QueryAsync<dynamic>(
            "SELECT PRODUCT_ID, SUM(PLAN_QTY - COALESCE(COMPLETE_QTY, 0)) AS QTY FROM POM_WORK_ORDER " +
            "WHERE STATUS IN ('Released', 'Started') AND PRODUCT_ID IS NOT NULL GROUP BY PRODUCT_ID", null, ct);
        foreach (var r in wo) map[(string)r.PRODUCT_ID] = map.GetValueOrDefault((string)r.PRODUCT_ID) + Convert.ToDecimal(r.QTY);
        return map;
    }

    private async Task<IReadOnlyDictionary<string, MrpItemParameters>> LoadPlanningAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            "SELECT ITEM_ID, SAFETY_STOCK, LEAD_TIME_DAYS, LOT_SIZE, MAKE_OR_BUY " +
            "FROM MDM_ITEM_PLANNING WHERE IS_ACTIVE = 'Y'", null, ct);
        var map = new Dictionary<string, MrpItemParameters>(StringComparer.Ordinal);
        foreach (var r in rows)
            map[(string)r.ITEM_ID] = new MrpItemParameters(
                Convert.ToDecimal(r.SAFETY_STOCK),
                r.LEAD_TIME_DAYS is null ? null : (int?)Convert.ToInt32(r.LEAD_TIME_DAYS),
                Convert.ToDecimal(r.LOT_SIZE),
                r.MAKE_OR_BUY as string);
        return map;
    }

    private async Task<IReadOnlyDictionary<string, MrpVendorParameters>> LoadVendorsAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<dynamic>(
            "SELECT PRODUCT_ID, MIN(LEAD_TIME_DAYS) AS LEAD_TIME_DAYS, MIN(MOQ) AS MOQ " +
            "FROM MDM_VENDOR_ITEM GROUP BY PRODUCT_ID", null, ct);
        var map = new Dictionary<string, MrpVendorParameters>(StringComparer.Ordinal);
        foreach (var r in rows)
            map[(string)r.PRODUCT_ID] = new MrpVendorParameters(
                r.LEAD_TIME_DAYS is null ? null : (int?)Convert.ToInt32(r.LEAD_TIME_DAYS),
                r.MOQ is null ? null : (decimal?)Convert.ToDecimal(r.MOQ));
        return map;
    }

    // SQLite는 TEXT, MSSQL은 DateTime — 양쪽을 관대하게 파싱한다.
    private static DateTime? AsDate(object? value) => value switch
    {
        null => null,
        DateTime dt => dt,
        string s when DateTime.TryParse(s, out var dt) => dt,
        _ => null,
    };
}
