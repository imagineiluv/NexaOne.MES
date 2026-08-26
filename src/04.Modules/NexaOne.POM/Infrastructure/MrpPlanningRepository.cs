using System.Security.Cryptography;
using System.Text;
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
    private const string FinalizeSql =
        "UPDATE MRP_RUN SET STATUS = @status, FINISHED_AT = @finishedAt, DEMAND_COUNT = @demandCount, " +
        "PLANNED_ORDER_COUNT = @orderCount, MESSAGE = @message, UPDATED_BY = @by WHERE RUN_ID = @runId";
    private readonly ServiceObjectProcessor _processor;

    public MrpPlanningRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<MrpRunResult> RunAsync(string executedBy, MrpRunOptions? options = null, CancellationToken ct = default)
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
            var receipts = await LoadScheduledReceiptsAsync(ct);
            // 총량 모드 onOrder = 일자 예정입고의 품목별 합(단일 출처 — 두 모드 간 집계 드리프트 방지).
            var onOrder = receipts.GroupBy(r => r.ItemId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty), StringComparer.Ordinal);
            var bucketOptions = options is null
                ? null
                : new MrpBucketOptions(DateTime.UtcNow.Date, options.BucketDays, options.HorizonBuckets);
            var result = MrpCalculator.Calculate(
                demands,
                await LoadBomAsync(ct),
                await LoadOnHandAsync(ct),
                onOrder,
                await LoadPlanningAsync(ct),
                await LoadVendorsAsync(ct),
                bucketOptions,
                receipts);

            if (!result.Success)
            {
                await FinalizeAsync(runId, "Failed", demands.Count, 0, result.Error, executedBy, CancellationToken.None);
                return new MrpRunResult(runId, "Failed", demands.Count, 0, result.Error);
            }

            // A run is published atomically: proposals, pegging and the Success marker become
            // visible together. CRP is derived from proposals, so it cannot observe a partial run.
            var statements = new List<(string Sql, object? Param)>();
            var seq = 0;
            foreach (var p in result.Proposals)
            {
                var plannedOrderId = $"{runId}_{++seq:D4}";
                statements.Add((
                    "INSERT INTO MRP_PLANNED_ORDER (PLANNED_ORDER_ID, RUN_ID, ITEM_ID, ORDER_TYPE, GROSS_QTY, " +
                    "ON_HAND_QTY, ON_ORDER_QTY, SAFETY_STOCK_QTY, NET_QTY, SUGGESTED_QTY, DUE_DATE, RELEASE_DATE, " +
                    "SOURCE_DEMAND, PLANT_ID, CREATED_BY, UPDATED_BY) " +
                    "VALUES (@id, @runId, @item, @type, @gross, @onHand, @onOrder, @safety, @net, @suggested, " +
                    "@due, @release, @source, @plant, @by, @by)",
                    new
                    {
                        id = plannedOrderId,
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
                    }));

                // 페깅(v2) — 총소요의 수요별 기여를 분해 보존(SOURCE_DEMAND 요약의 정밀판). append-only.
                var pseq = 0;
                foreach (var cb in p.Contributions ?? Array.Empty<MrpContribution>())
                    statements.Add((
                        "INSERT INTO MRP_PEGGING (PEGGING_ID, RUN_ID, PLANNED_ORDER_ID, ITEM_ID, DEMAND_REF, QTY, CREATED_BY) " +
                        "VALUES (@id, @runId, @orderId, @item, @demandRef, @qty, @by)",
                        new
                        {
                            id = $"{runId}_{seq:D4}_P{++pseq:D3}",
                            runId,
                            orderId = plannedOrderId,
                            item = p.ItemId,
                            demandRef = cb.DemandRef,
                            qty = cb.Qty,
                            by = executedBy,
                        }));
            }

            statements.Add((FinalizeSql, new
            {
                runId,
                status = "Success",
                finishedAt = DateTime.UtcNow.ToString(Ts),
                demandCount = demands.Count,
                orderCount = result.Proposals.Count,
                message = (string?)null,
                by = executedBy,
            }));
            await _processor.ExecuteManyAsync(ct, statements.ToArray());
            return new MrpRunResult(runId, "Success", demands.Count, result.Proposals.Count, null);
        }
        catch (Exception ex)
        {
            // 적재/계산 예외 — 런을 Failed로 마감해 이력에 사유를 남긴다(원자료 무변경이라 재실행 안전).
            var message = ex.Message.Length > 900 ? ex.Message[..900] : ex.Message;
            await FinalizeAsync(runId, "Failed", 0, 0, message, executedBy, CancellationToken.None);
            return new MrpRunResult(runId, "Failed", 0, 0, message);
        }
    }

    /// <summary>Proposed 제안→실오더 전환. Purchase는 PRC 구매오더로, Production은
    /// POM 생산계획과 생산관리지시로 전환한다. 공정 작업지시는 라우팅 전개 서비스가 별도로 생성한다.
    /// 원자성: 실오더 INSERT + 제안 Converted 마킹 전 문장을 단일 ExecuteManyAsync 트랜잭션으로 커밋
    /// (MixingPersistAsync/DATA-3 패턴 — 부분 커밋 불가). ⚠ ExecuteManyAsync는 감사컬럼 자동주입이
    /// 없어 CREATED_BY/UPDATED_BY를 문장에 명시한다(시각은 DDL DEFAULT).</summary>
    public async Task<MrpConvertResult> ConvertAsync(
        string? runId, IReadOnlyList<string>? plannedOrderIds,
        IReadOnlyList<MrpProductionAssignment>? productionAssignments, string executedBy, CancellationToken ct = default)
    {
        try
        {
            runId ??= (await QueryAsync<string>(
                "SELECT MAX(RUN_ID) FROM MRP_RUN WHERE STATUS = 'Success' " +
                "AND STARTED_AT = (SELECT MAX(STARTED_AT) FROM MRP_RUN WHERE STATUS = 'Success')", null, ct))
                .FirstOrDefault();
            if (runId is null)
                return new MrpConvertResult("", 0, 0, 0, "실행 이력이 없습니다 — 먼저 MRP를 실행하세요.");

            // 행 선택 전환(UX) — ids 지정 시 해당 제안만(Dapper 리스트 IN 확장). null=전량.
            var idFilter = plannedOrderIds is { Count: > 0 } ? " AND P.PLANNED_ORDER_ID IN @ids" : "";
            var rows = (await QueryAsync<dynamic>(
                "SELECT P.PLANNED_ORDER_ID, P.ITEM_ID, P.ORDER_TYPE, P.SUGGESTED_QTY, P.DUE_DATE, P.RELEASE_DATE, P.PLANT_ID " +
                "FROM MRP_PLANNED_ORDER P WHERE P.RUN_ID = @runId AND P.STATUS = 'Proposed' " +
                "AND EXISTS (SELECT 1 FROM MRP_RUN R WHERE R.RUN_ID = P.RUN_ID AND R.STATUS = 'Success')" + idFilter +
                " ORDER BY P.PLANNED_ORDER_ID",
                new { runId, ids = plannedOrderIds }, ct)).ToList();
            if (rows.Count == 0)
                return new MrpConvertResult(runId, 0, 0, 0, "전환 대상(Proposed)이 없습니다.");

            const string MarkSql =
                "UPDATE MRP_PLANNED_ORDER SET STATUS = 'Converted', CONVERTED_ORDER_ID = @newId, " +
                "UPDATED_BY = @by, UPDATED_AT = @now WHERE PLANNED_ORDER_ID = @id " +
                "AND RUN_ID = @runId AND STATUS = 'Proposed'";
            var now = DateTime.UtcNow.ToString(Ts);
            var statements = new List<(string Sql, object? Param)>();
            int purchaseOrders = 0, productionOrders = 0;

            var productionIds = rows
                .Where(r => !string.Equals((string)r.ORDER_TYPE, "Purchase", StringComparison.OrdinalIgnoreCase))
                .Select(r => (string)r.PLANNED_ORDER_ID)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var assignments = productionAssignments ?? Array.Empty<MrpProductionAssignment>();
            var duplicateAssignment = assignments
                .Where(a => !string.IsNullOrWhiteSpace(a.PlannedOrderId))
                .GroupBy(a => a.PlannedOrderId.Trim(), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(g => g.Count() > 1);
            if (duplicateAssignment is not null)
                return new MrpConvertResult(runId, 0, 0, 0,
                    $"생산 제안 {duplicateAssignment.Key}의 설비 배정이 중복되었습니다.");

            var assignmentMap = assignments
                .Where(a => !string.IsNullOrWhiteSpace(a.PlannedOrderId))
                .ToDictionary(a => a.PlannedOrderId.Trim(), a => a.EquipmentId?.Trim() ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);
            var missingAssignments = productionIds.Where(id => !assignmentMap.ContainsKey(id)).ToList();
            var unrelatedAssignments = assignmentMap.Keys.Where(id => !productionIds.Contains(id)).ToList();
            if (missingAssignments.Count > 0 || unrelatedAssignments.Count > 0)
                return new MrpConvertResult(runId, 0, 0, 0,
                    "Production 제안마다 선택된 행과 정확히 일치하는 설비 배정이 필요합니다.");

            var equipmentIds = assignmentMap.Values.Where(v => v.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var equipmentRows = equipmentIds.Count == 0
                ? new List<dynamic>()
                : (await QueryAsync<dynamic>(
                    "SELECT EQUIPMENT_ID, PLANT_ID, VALID_STATE FROM MDM_EQUIPMENT WHERE EQUIPMENT_ID IN @equipmentIds",
                    new { equipmentIds }, ct)).ToList();
            var equipmentMap = equipmentRows.ToDictionary(
                r => (string)r.EQUIPMENT_ID, r => r, StringComparer.OrdinalIgnoreCase);

            foreach (var r in rows)
            {
                var plannedId = (string)r.PLANNED_ORDER_ID;
                var item = (string)r.ITEM_ID;
                var qty = Convert.ToDecimal(r.SUGGESTED_QTY);
                var dueDate = AsDate(r.DUE_DATE);
                var releaseDate = AsDate(r.RELEASE_DATE);
                var plant = (r.PLANT_ID as string)?.Trim() ?? string.Empty;

                if (string.Equals((string)r.ORDER_TYPE, "Purchase", StringComparison.OrdinalIgnoreCase))
                {
                    if (plant.Length == 0 || qty <= 0)
                        return new MrpConvertResult(runId, 0, 0, 0,
                            $"구매 제안 {plannedId}의 공장 또는 수량이 올바르지 않습니다.");
                    var orderId = StableId("PUR", plannedId);
                    purchaseOrders++;
                    statements.Add((
                        "INSERT INTO PRC_PURCHASE_ORDER (PURCHASE_ORDER_ID, PLANT_ID, PURCHASE_ORDER_NAME, " +
                        "ORDER_DATE, INCOMING_DATE, ORDER_QTY, PRODUCT_ID, STATUS, DESCRIPTION, CREATED_BY, UPDATED_BY) " +
                        "VALUES (@id, @plant, @name, @orderDate, @incoming, @qty, @item, 'Ordered', @desc, @by, @by)",
                        new
                        {
                            id = orderId, plant, name = $"MRP 전환 — {item}", orderDate = now,
                            incoming = dueDate?.ToString(Ts),
                            qty, item, desc = $"MRP {runId} / {plannedId}", by = executedBy,
                        }));
                    statements.Add((MarkSql, new { newId = orderId, id = plannedId, runId, by = executedBy, now }));
                }
                else
                {
                    if (plant.Length == 0 || qty <= 0)
                        return new MrpConvertResult(runId, 0, 0, 0,
                            $"생산 제안 {plannedId}의 공장 또는 수량이 올바르지 않습니다.");
                    if (!assignmentMap.TryGetValue(plannedId, out var equipmentId) ||
                        equipmentId.Length == 0 || !equipmentMap.TryGetValue(equipmentId, out var equipment))
                        return new MrpConvertResult(runId, 0, 0, 0,
                            $"생산 제안 {plannedId}에 존재하는 설비를 배정해야 합니다.");
                    var equipmentPlant = ((string)equipment.PLANT_ID).Trim();
                    var validState = ((string)equipment.VALID_STATE).Trim();
                    if (!string.Equals(equipmentPlant, plant, StringComparison.OrdinalIgnoreCase))
                        return new MrpConvertResult(runId, 0, 0, 0,
                            $"설비 {equipmentId}의 공장이 생산 제안 {plannedId}와 일치하지 않습니다.");
                    if (!string.Equals(validState, "Active", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(validState, "Valid", StringComparison.OrdinalIgnoreCase))
                        return new MrpConvertResult(runId, 0, 0, 0,
                            $"설비 {equipmentId}은(는) 활성 상태가 아닙니다.");

                    var scheduledStart = releaseDate ?? DateTime.UtcNow;
                    var scheduledEnd = dueDate ?? scheduledStart;
                    if (scheduledEnd < scheduledStart)
                        return new MrpConvertResult(runId, 0, 0, 0,
                            $"생산 제안 {plannedId}의 완료일이 시작일보다 빠릅니다.");
                    var planId = StableId("PLN", plannedId);
                    var orderId = StableId("PDO", plannedId);
                    productionOrders++;
                    statements.Add((
                        "INSERT INTO POM_PRODUCTION_PLAN (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY, " +
                        "PLANNED_START_DATE, PLANNED_END_DATE, STATUS, REMARK, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT) " +
                        "VALUES (@id, @name, @plant, @item, @qty, @start, @end, 'Released', @remark, @by, @now, @by, @now)",
                        new
                        {
                            id = planId, name = $"MRP 생산계획 — {item}", plant, item, qty,
                            start = scheduledStart.ToString(Ts), end = scheduledEnd.ToString(Ts),
                            remark = $"MRP {runId} / {plannedId}", by = executedBy, now,
                        }));
                    statements.Add((
                        "INSERT INTO POM_PRODUCTION_ORDER (ORDER_ID, PLAN_ID, EQUIPMENT_ID, PRODUCT_ID, ORDER_QTY, " +
                        "ACTUAL_QTY, SCHEDULED_START, SCHEDULED_END, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT) " +
                        "VALUES (@id, @planId, @equipmentId, @item, @qty, NULL, @start, @end, 'Issued', @by, @now, @by, @now)",
                        new
                        {
                            id = orderId, planId, equipmentId, item, qty,
                            start = scheduledStart.ToString(Ts), end = scheduledEnd.ToString(Ts),
                            by = executedBy, now,
                        }));
                    statements.Add((MarkSql, new { newId = orderId, id = plannedId, runId, by = executedBy, now }));
                }
            }

            await _processor.ExecuteManyAsync(ct, statements.ToArray());
            return new MrpConvertResult(runId, purchaseOrders + productionOrders,
                purchaseOrders, productionOrders, null);
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
            FinalizeSql,
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

    private async Task<IReadOnlyList<MrpScheduledReceipt>> LoadScheduledReceiptsAsync(CancellationToken ct)
    {
        var list = new List<MrpScheduledReceipt>();
        // 예정입고 ①구매(V080 PRODUCT_ID 링크가 있는 행만 — 표준 갭 보수분, 입고예정일=INCOMING_DATE)
        // ②생산(생산관리지시 미완수량, 완료예정일=SCHEDULED_END). 공정 작업지시는 중복 집계하지 않는다.
        var po = await QueryAsync<dynamic>(
            "SELECT PRODUCT_ID, ORDER_QTY, INCOMING_DATE FROM PRC_PURCHASE_ORDER " +
            "WHERE STATUS IN ('Ordered', 'Incoming') AND PRODUCT_ID IS NOT NULL", null, ct);
        foreach (var r in po)
            list.Add(new MrpScheduledReceipt((string)r.PRODUCT_ID, Convert.ToDecimal(r.ORDER_QTY), AsDate(r.INCOMING_DATE)));
        var productionOrders = await QueryAsync<dynamic>(
            "SELECT PRODUCT_ID, (ORDER_QTY - COALESCE(ACTUAL_QTY, 0)) AS QTY, SCHEDULED_END " +
            "FROM POM_PRODUCTION_ORDER WHERE STATUS IN ('Issued', 'InProgress') AND PRODUCT_ID IS NOT NULL " +
            "AND ORDER_QTY > COALESCE(ACTUAL_QTY, 0)", null, ct);
        foreach (var r in productionOrders)
            list.Add(new MrpScheduledReceipt((string)r.PRODUCT_ID, Convert.ToDecimal(r.QTY), AsDate(r.SCHEDULED_END)));
        return list;
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

    private static string StableId(string kind, string plannedOrderId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(plannedOrderId));
        return $"MRP-{kind}-{Convert.ToHexString(bytes)[..32]}";
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
