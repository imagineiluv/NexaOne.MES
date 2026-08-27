using System.Security.Cryptography;
using System.Text;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Mrp;
using NexaOne.POM.Domain.Mrp;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.POM.Infrastructure;

/// <summary>
/// POM 소유 MRP 실행기입니다. 계산과 POM 실행/제안 원장만 소유하며 MDM·IVT·PRC 원자료와 PRC command는
/// 각 모듈의 축소 계약으로 위임합니다. SLS 수요는 소유 모듈이 생길 때까지 별도 전환 projection 뒤에 격리합니다.
/// </summary>
public sealed class MrpPlanningRepository : QueryRepository, IMrpPlanner
{
    private const string Ts = "yyyy-MM-dd HH:mm:ss";
    private const string FinalizeSql =
        "UPDATE MRP_RUN SET STATUS = @status, FINISHED_AT = @finishedAt, DEMAND_COUNT = @demandCount, " +
        "PLANNED_ORDER_COUNT = @orderCount, MESSAGE = @message, UPDATED_BY = @by WHERE RUN_ID = @runId";
    private readonly ServiceObjectProcessor _processor;
    private readonly IMrpDemandSource _demandSource;
    private readonly IMrpMasterDirectory _masterDirectory;
    private readonly IMrpInventoryDirectory _inventoryDirectory;
    private readonly IPurchaseOrderPlanningBridge _purchaseOrders;
    private readonly IEquipmentDirectory _equipmentDirectory;

    public MrpPlanningRepository(
        EesDataSource dataSource,
        IMrpDemandSource demandSource,
        IMrpMasterDirectory masterDirectory,
        IMrpInventoryDirectory inventoryDirectory,
        IPurchaseOrderPlanningBridge purchaseOrders,
        IEquipmentDirectory equipmentDirectory) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _demandSource = demandSource;
        _masterDirectory = masterDirectory;
        _inventoryDirectory = inventoryDirectory;
        _purchaseOrders = purchaseOrders;
        _equipmentDirectory = equipmentDirectory;
    }

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
            var demands = await _demandSource.GetOpenDemandsAsync(ct);
            var master = await _masterDirectory.GetSnapshotAsync(ct);
            var inventory = await _inventoryDirectory.GetBalancesAsync(ct);
            var receipts = (await _purchaseOrders.GetScheduledReceiptsAsync(ct))
                .Select(static receipt => new MrpScheduledReceipt(
                    receipt.ProductId, receipt.Quantity, receipt.IncomingDate))
                .Concat(await LoadProductionReceiptsAsync(ct))
                .ToArray();
            // 총량 모드 onOrder = 일자 예정입고의 품목별 합(단일 출처 — 두 모드 간 집계 드리프트 방지).
            var onOrder = receipts.GroupBy(r => r.ItemId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Sum(x => x.Qty), StringComparer.Ordinal);
            var bucketOptions = options is null
                ? null
                : new MrpBucketOptions(DateTime.UtcNow.Date, options.BucketDays, options.HorizonBuckets);
            var result = MrpCalculator.Calculate(
                demands,
                master.Bom.Select(static line => new MrpBomLine(
                    line.ProductId, line.ComponentId, line.Quantity, line.ScrapRate)).ToArray(),
                inventory.ToDictionary(
                    static balance => balance.MaterialId,
                    static balance => balance.Quantity,
                    StringComparer.Ordinal),
                onOrder,
                master.Items.ToDictionary(
                    static item => item.ItemId,
                    static item => new MrpItemParameters(
                        item.SafetyStock, item.LeadTimeDays, item.LotSize, item.MakeOrBuy),
                    StringComparer.Ordinal),
                master.Vendors.ToDictionary(
                    static vendor => vendor.ProductId,
                    static vendor => new MrpVendorParameters(
                        vendor.LeadTimeDays, vendor.MinimumOrderQuantity),
                    StringComparer.Ordinal),
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
    /// POM 생산오더/제안 마킹은 단일 로컬 트랜잭션입니다. PRC는 별도 소유 원장이므로 분산 트랜잭션 대신
    /// 안정 ID 기반 멱등 command를 먼저 보장하고, POM 커밋 실패 시 같은 요청 재실행으로 수렴합니다.</summary>
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
            var purchaseCommands = new List<(MrpPurchaseOrderRequest Request, string PlannedOrderId)>();
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
            var equipmentEntries = await Task.WhenAll(equipmentIds.Select(async id =>
                (Id: id, Entry: await _equipmentDirectory.GetEquipmentAsync(id, ct))));
            var equipmentMap = equipmentEntries
                .Where(static pair => pair.Entry is not null)
                .ToDictionary(static pair => pair.Id, static pair => pair.Entry!, StringComparer.OrdinalIgnoreCase);

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
                    purchaseCommands.Add((
                        new MrpPurchaseOrderRequest(
                            orderId,
                            plant,
                            $"MRP 전환 — {item}",
                            DateTime.UtcNow,
                            dueDate,
                            qty,
                            item,
                            $"MRP {runId} / {plannedId}",
                            executedBy),
                        plannedId));
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
                    var equipmentPlant = equipment.PlantId.Trim();
                    if (!string.Equals(equipmentPlant, plant, StringComparison.OrdinalIgnoreCase))
                        return new MrpConvertResult(runId, 0, 0, 0,
                            $"설비 {equipmentId}의 공장이 생산 제안 {plannedId}와 일치하지 않습니다.");
                    if (!equipment.IsValid)
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

            // 모든 제안과 설비를 먼저 검증한 뒤 외부 소유 command를 보낸다. PRC 성공 후 POM 로컬
            // 커밋이 실패해도 stable order id 덕분에 재실행은 기존 오더를 확인하고 제안 마킹으로 수렴한다.
            foreach (var command in purchaseCommands)
            {
                await _purchaseOrders.EnsureMrpPurchaseOrderAsync(command.Request, ct);
                statements.Add((MarkSql, new
                {
                    newId = command.Request.PurchaseOrderId,
                    id = command.PlannedOrderId,
                    runId,
                    by = executedBy,
                    now,
                }));
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

    private async Task<IReadOnlyList<MrpScheduledReceipt>> LoadProductionReceiptsAsync(CancellationToken ct)
    {
        var list = new List<MrpScheduledReceipt>();
        // POM 생산관리지시 미완수량만 계산한다. 구매 예정입고는 PRC 소유 bridge가 제공하며,
        // 공정 작업지시는 중복 집계하지 않는다.
        var productionOrders = await QueryAsync<dynamic>(
            "SELECT PRODUCT_ID, (ORDER_QTY - COALESCE(ACTUAL_QTY, 0)) AS QTY, SCHEDULED_END " +
            "FROM POM_PRODUCTION_ORDER WHERE STATUS IN ('Issued', 'InProgress') AND PRODUCT_ID IS NOT NULL " +
            "AND ORDER_QTY > COALESCE(ACTUAL_QTY, 0)", null, ct);
        foreach (var r in productionOrders)
            list.Add(new MrpScheduledReceipt((string)r.PRODUCT_ID, Convert.ToDecimal(r.QTY), AsDate(r.SCHEDULED_END)));
        return list;
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
