using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EMS.Infrastructure;

public sealed class SparePartRepository : QueryRepository, ISparePartRepository
{
    private readonly ServiceObjectProcessor _processor;

    public SparePartRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<SparePart?> GetByIdAsync(string partId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_SPARE_PART WHERE PART_ID = @partId";
        var row = await QueryFirstOrDefaultAsync<PartRow>(sql, new { partId }, ct);
        return row?.ToDomain();
    }

    public async Task<SparePartStockTransaction?> GetTransactionByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT i.INOUT_ID, i.PART_ID, i.TRANSACTION_TYPE, i.QUANTITY,
                                    i.BALANCE_BEFORE, i.BALANCE_AFTER, i.PROCESSED_BY,
                                    i.TRANSACTION_AT, i.IDEMPOTENCY_KEY, i.CLIENT_CHANNEL,
                                    i.DEVICE_ID, i.CORRELATION_ID, i.WO_ID, i.EQUIPMENT_ID,
                                    i.FROM_LOCATION, i.TO_LOCATION, i.REMARK,
                                    u.USAGE_ID, u.PART_ID AS USAGE_PART_ID,
                                    u.BOM_ITEM_ID, u.EQUIPMENT_ID AS USAGE_EQUIPMENT_ID,
                                    u.WO_ID AS USAGE_WO_ID, u.QUANTITY AS USAGE_QUANTITY,
                                    u.USED_BY, u.USED_AT, u.REMOVAL_REASON
                             FROM EMS_SPARE_PART_INOUT i
                             LEFT JOIN EMS_SPARE_PART_USAGE u ON u.INOUT_ID = i.INOUT_ID
                             WHERE i.IDEMPOTENCY_KEY = @idempotencyKey";
        var row = await QueryFirstOrDefaultAsync<TransactionRow>(sql, new { idempotencyKey }, ct);
        return row?.ToDomain();
    }

    public async Task<bool> IsUsageScopeValidAsync(
        string partId,
        string equipmentId,
        string? bomItemId,
        string? workOrderId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT COUNT(*)
            FROM MDM_EQUIPMENT e
            WHERE e.EQUIPMENT_ID = @equipmentId
              AND e.VALID_STATE = 'Valid'
              AND (@bomItemId IS NULL OR EXISTS (
                    SELECT 1 FROM EMS_EQUIPMENT_PART_BOM b
                    WHERE b.BOM_ITEM_ID = @bomItemId
                      AND b.PART_ID = @partId
                      AND b.IS_ACTIVE = 1
                      AND ((b.EQUIPMENT_ID IS NOT NULL AND b.EQUIPMENT_ID = e.EQUIPMENT_ID)
                           OR (b.EQUIPMENT_ID IS NULL
                               AND b.EQUIPMENT_CLASS_ID = e.EQUIPMENT_CLASS_ID))))
              AND (@workOrderId IS NULL OR EXISTS (
                    SELECT 1 FROM EMS_WORK_ORDER w
                    WHERE w.WO_ID = @workOrderId
                      AND w.EQUIPMENT_ID = e.EQUIPMENT_ID))";
        return await CountAsync(sql, new { partId, equipmentId, bomItemId, workOrderId }, ct) == 1;
    }

    public async Task<IReadOnlyList<SparePart>> GetAllAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_SPARE_PART ORDER BY PART_NAME";
        var rows = await QueryAsync<PartRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<SparePart>().ToList();
    }

    public async Task<IReadOnlyList<SparePart>> GetLowStockAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_SPARE_PART WHERE CURRENT_STOCK <= MIN_STOCK ORDER BY PART_NAME";
        var rows = await QueryAsync<PartRow>(sql, new { }, ct);
        return rows.Select(r => r.ToDomain()).OfType<SparePart>().ToList();
    }

    public async Task AddAsync(
        SparePart part,
        string actorId,
        CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO EMS_SPARE_PART
            (PART_ID, PART_NAME, PART_NUMBER, DESCRIPTION, UNIT_OF_MEASURE,
             CURRENT_STOCK, MIN_STOCK, MAX_STOCK, LOCATION, EQUIPMENT_CLASS_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PartId, @PartName, @PartNumber, @Description, @UnitOfMeasure,
             @CurrentStock, @MinStock, @MaxStock, @Location, @EquipmentClassId,
             @Actor, @Now, @Actor, @Now)";
        var row = PartRow.FromDomain(part);
        await _processor.ExecuteAsync(sql, new
        {
            row.PartId,
            row.PartName,
            row.PartNumber,
            row.Description,
            row.UnitOfMeasure,
            row.CurrentStock,
            row.MinStock,
            row.MaxStock,
            row.Location,
            row.EquipmentClassId,
            Actor = actorId,
            Now = DateTime.UtcNow,
        }, ct);
    }

    public async Task UpdateAsync(SparePart part, CancellationToken ct = default)
    {
        const string sql = @"UPDATE EMS_SPARE_PART SET
            CURRENT_STOCK = @CurrentStock, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PART_ID = @PartId";
        await _processor.UpdateAsync(sql, PartRow.FromDomain(part), ct);
    }

    public Task<bool> PersistAdjustmentAsync(
        SparePartStockTransaction transaction,
        CancellationToken ct = default)
    {
        var update = @"UPDATE EMS_SPARE_PART SET
                CURRENT_STOCK = @BalanceAfter, UPDATED_BY = @Actor, UPDATED_AT = @Now
                WHERE PART_ID = @PartId AND CURRENT_STOCK = @BalanceBefore";
        const string insert = @"INSERT INTO EMS_SPARE_PART_INOUT
            (INOUT_ID, PART_ID, TRANSACTION_TYPE, QUANTITY, FROM_LOCATION, TO_LOCATION,
             TRANSACTION_AT, PROCESSED_BY, REMARK, IDEMPOTENCY_KEY, CORRELATION_ID,
             WO_ID, EQUIPMENT_ID, BALANCE_BEFORE, BALANCE_AFTER, CLIENT_CHANNEL, DEVICE_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@InoutId, @PartId, @TransactionType, @Quantity, @FromLocation, @ToLocation,
             @TransactionAt, @Actor, @Remark, @IdempotencyKey, @CorrelationId,
             @WorkOrderId, @EquipmentId, @BalanceBefore, @BalanceAfter, @ClientChannel, @DeviceId,
             @Actor, @Now, @Actor, @Now)";
        var now = DateTime.UtcNow;
        var p = TransactionParam(transaction, now);
        if (transaction.Usage is null)
            return _processor.ExecuteGuardedManyAsync(ct, (update, p), (insert, p));

        update += @"
                AND EXISTS (
                    SELECT 1 FROM MDM_EQUIPMENT e
                    WHERE e.EQUIPMENT_ID = @UsageEquipmentId
                      AND e.VALID_STATE = 'Valid'
                      AND (@BomItemId IS NULL OR EXISTS (
                            SELECT 1 FROM EMS_EQUIPMENT_PART_BOM b
                            WHERE b.BOM_ITEM_ID = @BomItemId
                              AND b.PART_ID = @PartId
                              AND b.IS_ACTIVE = 1
                              AND ((b.EQUIPMENT_ID IS NOT NULL AND b.EQUIPMENT_ID = e.EQUIPMENT_ID)
                                   OR (b.EQUIPMENT_ID IS NULL
                                       AND b.EQUIPMENT_CLASS_ID = e.EQUIPMENT_CLASS_ID))))
                      AND (@WorkOrderId IS NULL OR EXISTS (
                            SELECT 1 FROM EMS_WORK_ORDER w
                            WHERE w.WO_ID = @WorkOrderId
                              AND w.EQUIPMENT_ID = e.EQUIPMENT_ID)))";
        const string insertUsage = @"INSERT INTO EMS_SPARE_PART_USAGE
            (USAGE_ID, INOUT_ID, PART_ID, BOM_ITEM_ID, EQUIPMENT_ID, WO_ID,
             QUANTITY, USED_BY, USED_AT, REMOVAL_REASON, CREATED_BY, CREATED_AT)
            VALUES
            (@UsageId, @InoutId, @PartId, @BomItemId, @UsageEquipmentId, @WorkOrderId,
             @Quantity, @Actor, @TransactionAt, @RemovalReason, @Actor, @Now)";
        return _processor.ExecuteGuardedManyAsync(
            ct, (update, p), (insert, p), (insertUsage, p));
    }

    private static Dapper.DynamicParameters TransactionParam(
        SparePartStockTransaction transaction,
        DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("InoutId", transaction.InoutId);
        p.Add("PartId", transaction.PartId);
        p.Add("TransactionType", transaction.TransactionType);
        p.Add("Quantity", transaction.Quantity);
        p.Add("BalanceBefore", transaction.BalanceBefore);
        p.Add("BalanceAfter", transaction.BalanceAfter);
        p.Add("Actor", transaction.ActorId);
        p.Add("TransactionAt", transaction.TransactionAt);
        p.Add("IdempotencyKey", transaction.IdempotencyKey);
        p.Add("ClientChannel", transaction.ClientChannel);
        p.Add("DeviceId", transaction.DeviceId);
        p.Add("CorrelationId", transaction.CorrelationId);
        p.Add("WorkOrderId", transaction.WorkOrderId);
        p.Add("EquipmentId", transaction.EquipmentId);
        p.Add("FromLocation", transaction.FromLocation);
        p.Add("ToLocation", transaction.ToLocation);
        p.Add("Remark", transaction.Remark);
        p.Add("UsageId", transaction.Usage?.UsageId);
        p.Add("BomItemId", transaction.Usage?.BomItemId);
        p.Add("UsageEquipmentId", transaction.Usage?.EquipmentId);
        p.Add("RemovalReason", transaction.Usage?.RemovalReason);
        p.Add("Now", now);
        return p;
    }

    private sealed class TransactionRow
    {
        public string InoutId { get; set; } = string.Empty;
        public string PartId { get; set; } = string.Empty;
        public string TransactionType { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public decimal BalanceBefore { get; set; }
        public decimal BalanceAfter { get; set; }
        public string ProcessedBy { get; set; } = string.Empty;
        public DateTime TransactionAt { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string ClientChannel { get; set; } = "MES";
        public string? DeviceId { get; set; }
        public string? CorrelationId { get; set; }
        public string? WoId { get; set; }
        public string? EquipmentId { get; set; }
        public string? FromLocation { get; set; }
        public string? ToLocation { get; set; }
        public string? Remark { get; set; }
        public string? UsageId { get; set; }
        public string? UsagePartId { get; set; }
        public string? BomItemId { get; set; }
        public string? UsageEquipmentId { get; set; }
        public string? UsageWoId { get; set; }
        public decimal? UsageQuantity { get; set; }
        public string? UsedBy { get; set; }
        public DateTime? UsedAt { get; set; }
        public string? RemovalReason { get; set; }

        public SparePartStockTransaction ToDomain()
        {
            var usage = string.IsNullOrWhiteSpace(UsageId)
                ? null
                : new SparePartUsage(
                    UsageId, InoutId, UsagePartId ?? PartId, BomItemId,
                    UsageEquipmentId ?? EquipmentId ?? string.Empty, UsageWoId,
                    UsageQuantity ?? Quantity, UsedBy ?? ProcessedBy,
                    UsedAt ?? TransactionAt, RemovalReason);
            return new SparePartStockTransaction(
                InoutId, PartId, TransactionType, Quantity, BalanceBefore, BalanceAfter,
                ProcessedBy, TransactionAt, IdempotencyKey, ClientChannel, DeviceId,
                CorrelationId, WoId, EquipmentId, FromLocation, ToLocation, Remark, usage);
        }
    }

    private sealed class PartRow
    {
        public string  PartId           { get; set; } = "";
        public string  PartName         { get; set; } = "";
        public string  PartNumber       { get; set; } = "";
        public string  Description      { get; set; } = "";
        public string  UnitOfMeasure    { get; set; } = "";
        public decimal CurrentStock     { get; set; }
        public decimal MinStock         { get; set; }
        public decimal MaxStock         { get; set; }
        public string  Location         { get; set; } = "";
        public string? EquipmentClassId { get; set; }
        public string   CreatedBy       { get; set; } = "";
        public DateTime  CreatedAt       { get; set; }
        public string?   UpdatedBy       { get; set; }
        public DateTime? UpdatedAt       { get; set; }

        public SparePart ToDomain() =>
            SparePart.Restore(PartId, PartName, PartNumber, Description, UnitOfMeasure,
                CurrentStock, MinStock, MaxStock, Location, EquipmentClassId,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static PartRow FromDomain(SparePart p) => new()
        {
            PartId = p.Id, PartName = p.PartName, PartNumber = p.PartNumber,
            Description = p.Description, UnitOfMeasure = p.UnitOfMeasure,
            CurrentStock = p.CurrentStock, MinStock = p.MinStock, MaxStock = p.MaxStock,
            Location = p.Location, EquipmentClassId = p.EquipmentClassId,
            CreatedBy = p.CreatedBy, CreatedAt = p.CreatedAt,
            UpdatedBy = p.UpdatedBy, UpdatedAt = p.UpdatedAt
        };
    }
}
