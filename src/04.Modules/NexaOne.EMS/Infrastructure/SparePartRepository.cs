using System.Data.Common;
using Microsoft.Data.Sqlite;
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
        string equipmentClassId,
        string? bomItemId,
        string? workOrderId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT COUNT(*)
            FROM EMS_SPARE_PART p
            WHERE p.PART_ID = @partId
              AND (@bomItemId IS NULL OR EXISTS (
                    SELECT 1 FROM EMS_EQUIPMENT_PART_BOM b
                    WHERE b.BOM_ITEM_ID = @bomItemId
                      AND b.PART_ID = @partId
                      AND b.IS_ACTIVE = 1
                      AND ((b.EQUIPMENT_ID IS NOT NULL AND b.EQUIPMENT_ID = @equipmentId)
                           OR (b.EQUIPMENT_ID IS NULL
                               AND b.EQUIPMENT_CLASS_ID = @equipmentClassId))))
              AND (@workOrderId IS NULL OR EXISTS (
                    SELECT 1 FROM EMS_WORK_ORDER w
                    WHERE w.WO_ID = @workOrderId
                      AND w.EQUIPMENT_ID = @equipmentId))";
        return await CountAsync(
            sql,
            new { partId, equipmentId, equipmentClassId, bomItemId, workOrderId },
            ct) == 1;
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

    public async Task<bool> TryAddWithOpeningBalanceAsync(
        SparePart part,
        SparePartStockTransaction openingBalance,
        CancellationToken ct = default)
    {
        if (!string.Equals(openingBalance.TransactionType, "Opening", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(openingBalance.PartId, part.Id, StringComparison.OrdinalIgnoreCase)
            || openingBalance.BalanceBefore != 0m
            || openingBalance.BalanceAfter != part.CurrentStock
            || openingBalance.Quantity != part.CurrentStock
            || openingBalance.Quantity < 0m
            || openingBalance.Usage is not null
            || openingBalance.WorkOrderId is not null
            || openingBalance.EquipmentId is not null
            || openingBalance.FromLocation is not null
            || !string.Equals(openingBalance.ToLocation, part.Location, StringComparison.Ordinal))
            return false;

        const string insertOpening = @"INSERT INTO EMS_SPARE_PART_INOUT
            (INOUT_ID, PART_ID, TRANSACTION_TYPE, QUANTITY, FROM_LOCATION, TO_LOCATION,
             TRANSACTION_AT, PROCESSED_BY, REMARK, IDEMPOTENCY_KEY, CORRELATION_ID,
             WO_ID, EQUIPMENT_ID, BALANCE_BEFORE, BALANCE_AFTER, CLIENT_CHANNEL, DEVICE_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@InoutId, @PartId, @TransactionType, @Quantity, NULL, @ToLocation,
             @TransactionAt, @Actor, @Remark, @IdempotencyKey, @CorrelationId,
             NULL, NULL, @BalanceBefore, @BalanceAfter, @ClientChannel, @DeviceId,
             @Actor, @Now, @Actor, @Now)";
        const string insertPart = @"INSERT INTO EMS_SPARE_PART
            (PART_ID, PART_NAME, PART_NUMBER, DESCRIPTION, UNIT_OF_MEASURE,
             CURRENT_STOCK, MIN_STOCK, MAX_STOCK, LOCATION, EQUIPMENT_CLASS_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PartId, @PartName, @PartNumber, @Description, @UnitOfMeasure,
             @CurrentStock, @MinStock, @MaxStock, @Location, @EquipmentClassId,
             @Actor, @Now, @Actor, @Now)";
        var now = DateTime.UtcNow;
        var p = TransactionParam(openingBalance, now);
        p.Add("PartName", part.PartName);
        p.Add("PartNumber", part.PartNumber);
        p.Add("Description", part.Description);
        p.Add("UnitOfMeasure", part.UnitOfMeasure);
        p.Add("CurrentStock", part.CurrentStock);
        p.Add("MinStock", part.MinStock);
        p.Add("MaxStock", part.MaxStock);
        p.Add("Location", part.Location);
        p.Add("EquipmentClassId", part.EquipmentClassId);
        try
        {
            // The soft-reference ledger is intentionally inserted first. If the master insert
            // loses or fails, ExecuteGuardedManyAsync rolls both statements back.
            return await _processor.ExecuteGuardedManyAsync(
                ct,
                (insertOpening, p),
                (insertPart, p));
        }
        catch (DbException exception) when (IsUniqueViolation(exception))
        {
            return false;
        }
    }

    public async Task<bool> PersistAdjustmentAsync(
        SparePartStockTransaction transaction,
        string? equipmentClassId,
        CancellationToken ct = default)
    {
        if (!HasValidLedgerShape(transaction))
            return false;

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
        try
        {
            if (transaction.Usage is null)
                return await _processor.ExecuteGuardedManyAsync(ct, (update, p), (insert, p));

            update += @"
                AND (@BomItemId IS NULL OR EXISTS (
                    SELECT 1 FROM EMS_EQUIPMENT_PART_BOM b
                    WHERE b.BOM_ITEM_ID = @BomItemId
                      AND b.PART_ID = @PartId
                      AND b.IS_ACTIVE = 1
                      AND ((b.EQUIPMENT_ID IS NOT NULL
                            AND b.EQUIPMENT_ID = @UsageEquipmentId)
                           OR (b.EQUIPMENT_ID IS NULL
                               AND b.EQUIPMENT_CLASS_ID = @UsageEquipmentClassId))))
                AND (@WorkOrderId IS NULL OR EXISTS (
                    SELECT 1 FROM EMS_WORK_ORDER w
                    WHERE w.WO_ID = @WorkOrderId
                      AND w.EQUIPMENT_ID = @UsageEquipmentId))";
            const string insertUsage = @"INSERT INTO EMS_SPARE_PART_USAGE
            (USAGE_ID, INOUT_ID, PART_ID, BOM_ITEM_ID, EQUIPMENT_ID, WO_ID,
             QUANTITY, USED_BY, USED_AT, REMOVAL_REASON, CREATED_BY, CREATED_AT)
            VALUES
            (@UsageId, @InoutId, @PartId, @BomItemId, @UsageEquipmentId, @WorkOrderId,
             @Quantity, @Actor, @TransactionAt, @RemovalReason, @Actor, @Now)";
            p.Add("UsageEquipmentClassId", equipmentClassId);
            return await _processor.ExecuteGuardedManyAsync(
                ct, (update, p), (insert, p), (insertUsage, p));
        }
        catch (DbException exception) when (IsAdjustmentIdempotencyRace(exception))
        {
            return false;
        }
    }

    private static bool HasValidLedgerShape(SparePartStockTransaction transaction)
    {
        var delta = transaction.Delta;
        if (delta == 0m || transaction.Quantity != Math.Abs(delta)) return false;

        var isUsage = string.Equals(transaction.TransactionType, "Usage", StringComparison.OrdinalIgnoreCase);
        var allowedType = delta > 0m
            ? string.Equals(transaction.TransactionType, "Incoming", StringComparison.OrdinalIgnoreCase)
              || string.Equals(transaction.TransactionType, "Adjustment", StringComparison.OrdinalIgnoreCase)
            : isUsage
              || string.Equals(transaction.TransactionType, "Scrap", StringComparison.OrdinalIgnoreCase)
              || string.Equals(transaction.TransactionType, "Adjustment", StringComparison.OrdinalIgnoreCase);
        if (!allowedType) return false;
        if (!isUsage) return transaction.Usage is null;

        var usage = transaction.Usage;
        return usage is not null
               && !string.IsNullOrWhiteSpace(transaction.EquipmentId)
               && !string.IsNullOrWhiteSpace(usage.EquipmentId)
               && usage.Quantity == transaction.Quantity
               && string.Equals(usage.InoutId, transaction.InoutId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(usage.PartId, transaction.PartId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(usage.EquipmentId, transaction.EquipmentId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(usage.WorkOrderId, transaction.WorkOrderId, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAdjustmentIdempotencyRace(DbException exception)
    {
        if (!IsUniqueViolation(exception)) return false;
        return exception.Message.Contains("UX_EMS_SPARE_PART_INOUT_IDEMPOTENCY", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "EMS_SPARE_PART_INOUT.IDEMPOTENCY_KEY",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUniqueViolation(DbException exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                  && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when string.Equals(
                exception.GetType().FullName,
                "Microsoft.Data.SqlClient.SqlException",
                StringComparison.Ordinal)
            => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
               && number is 2601 or 2627,
        _ => false,
    };

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

    }
}
