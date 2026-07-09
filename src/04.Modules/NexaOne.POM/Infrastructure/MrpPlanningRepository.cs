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
                    "SOURCE_DEMAND, CREATED_BY, UPDATED_BY) " +
                    "VALUES (@id, @runId, @item, @type, @gross, @onHand, @onOrder, @safety, @net, @suggested, " +
                    "@due, @release, @source, @by, @by)",
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
            "SELECT SALES_ORDER_ID, PRODUCT_ID, (PLAN_QTY - COALESCE(DELIVERED_QTY, 0)) AS OPEN_QTY, PLAN_END_DATE " +
            "FROM SLS_SALES_ORDER " +
            "WHERE STATUS IN ('Confirmed', 'Producing') AND PRODUCT_ID IS NOT NULL " +
            "  AND (PLAN_QTY - COALESCE(DELIVERED_QTY, 0)) > 0", null, ct);
        return rows.Select(r => new MrpDemand(
            (string)r.PRODUCT_ID, Convert.ToDecimal(r.OPEN_QTY), AsDate(r.PLAN_END_DATE), (string)r.SALES_ORDER_ID)).ToList();
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
