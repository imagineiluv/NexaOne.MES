using System.Data.Common;
using Microsoft.Data.Sqlite;
using NexaOne.EMS.Application.SpareParts;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EMS.Infrastructure;

public sealed class SparePartManagementRepository : QueryRepository, ISparePartManagementRepository
{
    private readonly ServiceObjectProcessor _processor;

    public SparePartManagementRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<bool> PartExistsAsync(string partId, CancellationToken ct = default)
        => await CountAsync("SELECT COUNT(*) FROM EMS_SPARE_PART WHERE PART_ID=@partId", new { partId }, ct) > 0;

    public async Task<SparePartMasterCommandRecord?> GetCommandAsync(
        string idempotencyKey,
        CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<CommandRow>(
            CommandSelect + " WHERE IDEMPOTENCY_KEY=@idempotencyKey",
            new { idempotencyKey }, ct))?.ToRecord();

    public async Task<SparePartStockPolicyRecord?> GetStockPolicyAsync(string partId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<PolicyRow>(PolicySelect + " WHERE PART_ID=@partId", new { partId }, ct))?.ToRecord();

    public async Task<SparePartStockPolicyRecord?> GetStockPolicyByIdempotencyKeyAsync(string key, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<PolicyRow>(PolicySelect + " WHERE LAST_IDEMPOTENCY_KEY=@key", new { key }, ct))?.ToRecord();

    public async Task<bool> TryCreateStockPolicyAsync(SparePartStockPolicyRecord record, CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertPolicySql, PolicyParam(record)));
        }
        catch (DbException exception) when (IsExpectedUniqueRace(exception, "EMS_SPARE_PART_STOCK_POLICY"))
        {
            return false;
        }
    }

    public async Task<bool> TryUpdateStockPolicyAsync(
        SparePartStockPolicyRecord record,
        int expectedVersion,
        CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct, (UpdatePolicySql, PolicyParam(record, expectedVersion)));
        }
        catch (DbException exception) when (IsExpectedUniqueRace(
                   exception, "EMS_SPARE_PART_STOCK_POLICY"))
        {
            return false;
        }
    }

    public Task<bool> TryCreateStockPolicyAsync(
        SparePartStockPolicyRecord record,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default)
        => TryWriteWithCommandAsync(InsertPolicySql, PolicyParam(record), command, ct);

    public Task<bool> TryUpdateStockPolicyAsync(
        SparePartStockPolicyRecord record,
        int expectedVersion,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default)
        => TryWriteWithCommandAsync(
            UpdatePolicySql, PolicyParam(record, expectedVersion), command, ct);

    public async Task<SparePartSupplierRecord?> GetSupplierAsync(string partSupplierId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<SupplierRow>(SupplierSelect + " WHERE PART_SUPPLIER_ID=@partSupplierId", new { partSupplierId }, ct))?.ToRecord();

    public async Task<SparePartSupplierRecord?> GetSupplierByIdempotencyKeyAsync(string key, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<SupplierRow>(SupplierSelect + " WHERE LAST_IDEMPOTENCY_KEY=@key", new { key }, ct))?.ToRecord();

    public async Task<bool> HasOtherActivePrimarySupplierAsync(
        string partId,
        string partSupplierId,
        CancellationToken ct = default)
        => await CountAsync(@"SELECT COUNT(*) FROM EMS_SPARE_PART_SUPPLIER
            WHERE PART_ID=@partId AND PART_SUPPLIER_ID<>@partSupplierId
              AND IS_PRIMARY=1 AND IS_ACTIVE=1", new { partId, partSupplierId }, ct) > 0;

    public async Task<bool> TryCreateSupplierAsync(SparePartSupplierRecord record, CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertSupplierSql, SupplierParam(record)));
        }
        catch (DbException exception) when (IsExpectedUniqueRace(exception, "EMS_SPARE_PART_SUPPLIER"))
        {
            return false;
        }
    }

    public async Task<bool> TryUpdateSupplierAsync(SparePartSupplierRecord record, int expectedVersion, CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (UpdateSupplierSql, SupplierParam(record, expectedVersion)));
        }
        catch (DbException exception) when (IsExpectedUniqueRace(exception, "EMS_SPARE_PART_SUPPLIER"))
        {
            return false;
        }
    }

    public Task<bool> TryCreateSupplierAsync(
        SparePartSupplierRecord record,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default)
        => TryWriteWithCommandAsync(InsertSupplierSql, SupplierParam(record), command, ct);

    public Task<bool> TryUpdateSupplierAsync(
        SparePartSupplierRecord record,
        int expectedVersion,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default)
        => TryWriteWithCommandAsync(
            UpdateSupplierSql, SupplierParam(record, expectedVersion), command, ct);

    public async Task<EquipmentPartBomRecord?> GetEquipmentBomAsync(string bomItemId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<BomRow>(BomSelect + " WHERE BOM_ITEM_ID=@bomItemId", new { bomItemId }, ct))?.ToRecord();

    public async Task<EquipmentPartBomRecord?> GetEquipmentBomByIdempotencyKeyAsync(string key, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<BomRow>(BomSelect + " WHERE LAST_IDEMPOTENCY_KEY=@key", new { key }, ct))?.ToRecord();

    public async Task<bool> TryCreateEquipmentBomAsync(EquipmentPartBomRecord record, CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertBomSql, BomParam(record)));
        }
        catch (DbException exception) when (IsExpectedUniqueRace(exception, "EMS_EQUIPMENT_PART_BOM"))
        {
            return false;
        }
    }

    public async Task<bool> TryUpdateEquipmentBomAsync(
        EquipmentPartBomRecord record,
        int expectedVersion,
        CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct, (UpdateBomSql, BomParam(record, expectedVersion)));
        }
        catch (DbException exception) when (IsExpectedUniqueRace(
                   exception, "EMS_EQUIPMENT_PART_BOM"))
        {
            return false;
        }
    }

    public Task<bool> TryCreateEquipmentBomAsync(
        EquipmentPartBomRecord record,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default)
        => TryWriteWithCommandAsync(InsertBomSql, BomParam(record), command, ct);

    public Task<bool> TryUpdateEquipmentBomAsync(
        EquipmentPartBomRecord record,
        int expectedVersion,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default)
        => TryWriteWithCommandAsync(
            UpdateBomSql, BomParam(record, expectedVersion), command, ct);

    public async Task<SparePartReplenishmentInput?> GetReplenishmentInputAsync(
        string partId,
        CancellationToken ct = default)
    {
        var policy = await GetStockPolicyAsync(partId, ct);
        if (policy is null) return null;
        var stock = await QueryFirstOrDefaultAsync<StockRow>(
            "SELECT PART_ID AS PartId, CURRENT_STOCK AS CurrentStock FROM EMS_SPARE_PART WHERE PART_ID=@partId",
            new { partId }, ct);
        if (stock is null) return null;
        var suppliers = await QueryAsync<SupplierRow>(
            SupplierSelect + " WHERE PART_ID=@partId AND IS_ACTIVE=1", new { partId }, ct);
        return new SparePartReplenishmentInput(
            partId, Decimal(stock.CurrentStock), policy, suppliers.Select(x => x.ToRecord()).ToArray());
    }

    private const string PolicySelect = @"SELECT
        PART_ID AS PartId, SAFETY_STOCK AS SafetyStock, REORDER_POINT AS ReorderPoint,
        TARGET_STOCK AS TargetStock, RESERVED_QTY AS ReservedQuantity,
        AVG_DAILY_USAGE AS AverageDailyUsage, SERVICE_LEVEL AS ServiceLevel,
        REVIEW_CYCLE_DAYS AS ReviewCycleDays, IS_ACTIVE AS IsActive,
        VERSION_NO AS Version, LAST_IDEMPOTENCY_KEY AS LastIdempotencyKey,
        LAST_REQUEST_HASH AS LastRequestHash, CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt,
        UPDATED_BY AS UpdatedBy, UPDATED_AT AS UpdatedAt
        FROM EMS_SPARE_PART_STOCK_POLICY";

    private const string SupplierSelect = @"SELECT
        PART_SUPPLIER_ID AS PartSupplierId, PART_ID AS PartId, VENDOR_ID AS VendorId,
        VENDOR_PART_NO AS VendorPartNumber, LEAD_TIME_DAYS AS LeadTimeDays,
        MOQ AS MinimumOrderQuantity, UNIT_PRICE AS UnitPrice, CURRENCY AS Currency,
        IS_PRIMARY AS IsPrimary, IS_ACTIVE AS IsActive, VERSION_NO AS Version,
        LAST_IDEMPOTENCY_KEY AS LastIdempotencyKey, LAST_REQUEST_HASH AS LastRequestHash,
        CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt, UPDATED_BY AS UpdatedBy,
        UPDATED_AT AS UpdatedAt FROM EMS_SPARE_PART_SUPPLIER";

    private const string BomSelect = @"SELECT
        BOM_ITEM_ID AS BomItemId, EQUIPMENT_ID AS EquipmentId,
        EQUIPMENT_CLASS_ID AS EquipmentClassId, PART_ID AS PartId,
        QUANTITY_PER AS QuantityPer, CRITICALITY AS Criticality,
        REPLACEMENT_CYCLE_DAYS AS ReplacementCycleDays,
        REPLACEMENT_CYCLE_COUNT AS ReplacementCycleCount, POSITION_CODE AS PositionCode,
        IS_ACTIVE AS IsActive, VERSION_NO AS Version,
        LAST_IDEMPOTENCY_KEY AS LastIdempotencyKey, LAST_REQUEST_HASH AS LastRequestHash,
        CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt, UPDATED_BY AS UpdatedBy,
        UPDATED_AT AS UpdatedAt FROM EMS_EQUIPMENT_PART_BOM";

    private const string CommandSelect = @"SELECT
        COMMAND_ID AS CommandId, ENTITY_TYPE AS EntityType, ENTITY_ID AS EntityId,
        IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
        EXPECTED_VERSION AS ExpectedVersion, RESULT_VERSION AS ResultVersion,
        RESULT_JSON AS ResultJson, ACTOR_ID AS ActorId, CREATED_AT AS CreatedAt
        FROM EMS_SPARE_MASTER_COMMAND";

    private const string InsertPolicySql = @"INSERT INTO EMS_SPARE_PART_STOCK_POLICY
        (PART_ID, SAFETY_STOCK, REORDER_POINT, TARGET_STOCK, RESERVED_QTY,
         AVG_DAILY_USAGE, SERVICE_LEVEL, REVIEW_CYCLE_DAYS, IS_ACTIVE, VERSION_NO,
         LAST_IDEMPOTENCY_KEY, LAST_REQUEST_HASH, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @PartId, @SafetyStock, @ReorderPoint, @TargetStock, @ReservedQuantity,
               @AverageDailyUsage, @ServiceLevel, @ReviewCycleDays, @IsActive, @Version,
               @LastIdempotencyKey, @LastRequestHash, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt
        WHERE EXISTS (SELECT 1 FROM EMS_SPARE_PART WHERE PART_ID=@PartId)
          AND NOT EXISTS (SELECT 1 FROM EMS_SPARE_PART_STOCK_POLICY WHERE PART_ID=@PartId)
          AND NOT EXISTS (SELECT 1 FROM EMS_SPARE_PART_STOCK_POLICY WHERE LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey)";

    private const string UpdatePolicySql = @"UPDATE EMS_SPARE_PART_STOCK_POLICY SET
        SAFETY_STOCK=@SafetyStock, REORDER_POINT=@ReorderPoint, TARGET_STOCK=@TargetStock,
        RESERVED_QTY=@ReservedQuantity, AVG_DAILY_USAGE=@AverageDailyUsage,
        SERVICE_LEVEL=@ServiceLevel, REVIEW_CYCLE_DAYS=@ReviewCycleDays, IS_ACTIVE=@IsActive,
        VERSION_NO=@Version, LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey,
        LAST_REQUEST_HASH=@LastRequestHash, UPDATED_BY=@UpdatedBy, UPDATED_AT=@UpdatedAt
        WHERE PART_ID=@PartId AND VERSION_NO=@ExpectedVersion
          AND NOT EXISTS (SELECT 1 FROM EMS_SPARE_PART_STOCK_POLICY x
                          WHERE x.LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey AND x.PART_ID<>@PartId)";

    private const string InsertSupplierSql = @"INSERT INTO EMS_SPARE_PART_SUPPLIER
        (PART_SUPPLIER_ID, PART_ID, VENDOR_ID, VENDOR_PART_NO, LEAD_TIME_DAYS, MOQ,
         UNIT_PRICE, CURRENCY, IS_PRIMARY, IS_ACTIVE, VERSION_NO, LAST_IDEMPOTENCY_KEY,
         LAST_REQUEST_HASH, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @PartSupplierId, @PartId, @VendorId, @VendorPartNumber, @LeadTimeDays,
               @MinimumOrderQuantity, @UnitPrice, @Currency, @IsPrimary, @IsActive, @Version,
               @LastIdempotencyKey, @LastRequestHash, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt
        WHERE EXISTS (SELECT 1 FROM EMS_SPARE_PART WHERE PART_ID=@PartId)
          AND NOT EXISTS (SELECT 1 FROM EMS_SPARE_PART_SUPPLIER WHERE PART_SUPPLIER_ID=@PartSupplierId)
          AND NOT EXISTS (SELECT 1 FROM EMS_SPARE_PART_SUPPLIER WHERE LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey)
          AND (@IsPrimary=0 OR @IsActive=0 OR NOT EXISTS (
              SELECT 1 FROM EMS_SPARE_PART_SUPPLIER
              WHERE PART_ID=@PartId AND IS_PRIMARY=1 AND IS_ACTIVE=1))";

    private const string UpdateSupplierSql = @"UPDATE EMS_SPARE_PART_SUPPLIER SET
        PART_ID=@PartId, VENDOR_ID=@VendorId, VENDOR_PART_NO=@VendorPartNumber,
        LEAD_TIME_DAYS=@LeadTimeDays, MOQ=@MinimumOrderQuantity, UNIT_PRICE=@UnitPrice,
        CURRENCY=@Currency, IS_PRIMARY=@IsPrimary, IS_ACTIVE=@IsActive, VERSION_NO=@Version,
        LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey, LAST_REQUEST_HASH=@LastRequestHash,
        UPDATED_BY=@UpdatedBy, UPDATED_AT=@UpdatedAt
        WHERE PART_SUPPLIER_ID=@PartSupplierId AND VERSION_NO=@ExpectedVersion
          AND EXISTS (SELECT 1 FROM EMS_SPARE_PART WHERE PART_ID=@PartId)
          AND NOT EXISTS (SELECT 1 FROM EMS_SPARE_PART_SUPPLIER x
                          WHERE x.LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey
                            AND x.PART_SUPPLIER_ID<>@PartSupplierId)
          AND (@IsPrimary=0 OR @IsActive=0 OR NOT EXISTS (
              SELECT 1 FROM EMS_SPARE_PART_SUPPLIER x
              WHERE x.PART_ID=@PartId AND x.PART_SUPPLIER_ID<>@PartSupplierId
                AND x.IS_PRIMARY=1 AND x.IS_ACTIVE=1))";

    private const string InsertBomSql = @"INSERT INTO EMS_EQUIPMENT_PART_BOM
        (BOM_ITEM_ID, EQUIPMENT_ID, EQUIPMENT_CLASS_ID, PART_ID, QUANTITY_PER,
         CRITICALITY, REPLACEMENT_CYCLE_DAYS, REPLACEMENT_CYCLE_COUNT, POSITION_CODE,
         IS_ACTIVE, VERSION_NO, LAST_IDEMPOTENCY_KEY, LAST_REQUEST_HASH,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @BomItemId, @EquipmentId, @EquipmentClassId, @PartId, @QuantityPer,
               @Criticality, @ReplacementCycleDays, @ReplacementCycleCount, @PositionCode,
               @IsActive, @Version, @LastIdempotencyKey, @LastRequestHash,
               @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt
        WHERE EXISTS (SELECT 1 FROM EMS_SPARE_PART WHERE PART_ID=@PartId)
          AND NOT EXISTS (SELECT 1 FROM EMS_EQUIPMENT_PART_BOM WHERE BOM_ITEM_ID=@BomItemId)
          AND NOT EXISTS (SELECT 1 FROM EMS_EQUIPMENT_PART_BOM WHERE LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey)";

    private const string UpdateBomSql = @"UPDATE EMS_EQUIPMENT_PART_BOM SET
        EQUIPMENT_ID=@EquipmentId, EQUIPMENT_CLASS_ID=@EquipmentClassId, PART_ID=@PartId,
        QUANTITY_PER=@QuantityPer, CRITICALITY=@Criticality,
        REPLACEMENT_CYCLE_DAYS=@ReplacementCycleDays,
        REPLACEMENT_CYCLE_COUNT=@ReplacementCycleCount, POSITION_CODE=@PositionCode,
        IS_ACTIVE=@IsActive, VERSION_NO=@Version, LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey,
        LAST_REQUEST_HASH=@LastRequestHash, UPDATED_BY=@UpdatedBy, UPDATED_AT=@UpdatedAt
        WHERE BOM_ITEM_ID=@BomItemId AND VERSION_NO=@ExpectedVersion
          AND EXISTS (SELECT 1 FROM EMS_SPARE_PART WHERE PART_ID=@PartId)
          AND NOT EXISTS (SELECT 1 FROM EMS_EQUIPMENT_PART_BOM x
                          WHERE x.LAST_IDEMPOTENCY_KEY=@LastIdempotencyKey AND x.BOM_ITEM_ID<>@BomItemId)";

    private const string InsertCommandSql = @"INSERT INTO EMS_SPARE_MASTER_COMMAND
        (COMMAND_ID, ENTITY_TYPE, ENTITY_ID, IDEMPOTENCY_KEY, REQUEST_HASH,
         EXPECTED_VERSION, RESULT_VERSION, RESULT_JSON, ACTOR_ID, CREATED_AT)
        VALUES
        (@CommandId, @EntityType, @EntityId, @CommandIdempotencyKey, @CommandRequestHash,
         @CommandExpectedVersion, @ResultVersion, @ResultJson, @ActorId, @CommandCreatedAt)";

    private async Task<bool> TryWriteWithCommandAsync(
        string writeSql,
        object entityParameters,
        SparePartMasterCommandRecord command,
        CancellationToken ct)
    {
        var parameters = new Dapper.DynamicParameters(entityParameters);
        parameters.Add("CommandId", command.CommandId);
        parameters.Add("EntityType", command.EntityType);
        parameters.Add("EntityId", command.EntityId);
        parameters.Add("CommandIdempotencyKey", command.IdempotencyKey);
        parameters.Add("CommandRequestHash", command.RequestHash);
        parameters.Add("CommandExpectedVersion", command.ExpectedVersion);
        parameters.Add("ResultVersion", command.ResultVersion);
        parameters.Add("ResultJson", command.ResultJson);
        parameters.Add("ActorId", command.ActorId);
        parameters.Add("CommandCreatedAt", command.CreatedAt);
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct, (writeSql, parameters), (InsertCommandSql, parameters));
        }
        catch (DbException exception)
        {
            if (IsUniqueViolation(exception)
                || await GetCommandAsync(command.IdempotencyKey, ct) is not null)
                return false;
            throw;
        }
    }

    private static object PolicyParam(SparePartStockPolicyRecord x, int? expectedVersion = null) => new
    {
        x.PartId, x.SafetyStock, x.ReorderPoint, x.TargetStock, x.ReservedQuantity,
        x.AverageDailyUsage, x.ServiceLevel, x.ReviewCycleDays, x.IsActive, x.Version,
        x.LastIdempotencyKey, x.LastRequestHash, x.CreatedBy, x.CreatedAt, x.UpdatedBy,
        x.UpdatedAt, ExpectedVersion = expectedVersion,
    };

    private static object SupplierParam(SparePartSupplierRecord x, int? expectedVersion = null) => new
    {
        x.PartSupplierId, x.PartId, x.VendorId, x.VendorPartNumber, x.LeadTimeDays,
        x.MinimumOrderQuantity, x.UnitPrice, x.Currency, x.IsPrimary, x.IsActive,
        x.Version, x.LastIdempotencyKey, x.LastRequestHash, x.CreatedBy, x.CreatedAt,
        x.UpdatedBy, x.UpdatedAt, ExpectedVersion = expectedVersion,
    };

    private static object BomParam(EquipmentPartBomRecord x, int? expectedVersion = null) => new
    {
        x.BomItemId, x.EquipmentId, x.EquipmentClassId, x.PartId, x.QuantityPer,
        x.Criticality, x.ReplacementCycleDays, x.ReplacementCycleCount, x.PositionCode,
        x.IsActive, x.Version, x.LastIdempotencyKey, x.LastRequestHash, x.CreatedBy,
        x.CreatedAt, x.UpdatedBy, x.UpdatedAt, ExpectedVersion = expectedVersion,
    };

    private static bool IsExpectedUniqueRace(DbException exception, string table)
    {
        var unique = exception switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException", StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        return unique && exception.Message.Contains(table, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUniqueViolation(DbException exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                  && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when string.Equals(exception.GetType().FullName,
                "Microsoft.Data.SqlClient.SqlException", StringComparison.Ordinal)
            => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
               && number is 2601 or 2627,
        _ => false,
    };

    // Microsoft.Data.Sqlite may expose DECIMAL affinity as Int64, Double, Decimal, or text,
    // depending on the inserted value. Mapping straight to decimal? makes Dapper call a typed
    // reader accessor that fails for REAL values; normalize provider values at this boundary.
    private static decimal Decimal(object value)
        => Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);

    private static decimal? NullableDecimal(object? value)
        => value is null or DBNull ? null : Decimal(value);

    private sealed class StockRow
    {
        public string PartId { get; set; } = "";
        public object CurrentStock { get; set; } = 0m;
    }

    private sealed class PolicyRow
    {
        public string PartId { get; set; } = "";
        public object SafetyStock { get; set; } = 0m;
        public object ReorderPoint { get; set; } = 0m;
        public object TargetStock { get; set; } = 0m;
        public object ReservedQuantity { get; set; } = 0m;
        public object AverageDailyUsage { get; set; } = 0m;
        public object? ServiceLevel { get; set; }
        public int? ReviewCycleDays { get; set; }
        public bool IsActive { get; set; }
        public int Version { get; set; }
        public string LastIdempotencyKey { get; set; } = "";
        public string LastRequestHash { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public SparePartStockPolicyRecord ToRecord() => new(
            PartId, Decimal(SafetyStock), Decimal(ReorderPoint), Decimal(TargetStock),
            Decimal(ReservedQuantity), Decimal(AverageDailyUsage), NullableDecimal(ServiceLevel),
            ReviewCycleDays, IsActive, Version,
            LastIdempotencyKey, LastRequestHash, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);
    }

    private sealed class SupplierRow
    {
        public string PartSupplierId { get; set; } = "";
        public string PartId { get; set; } = "";
        public string VendorId { get; set; } = "";
        public string? VendorPartNumber { get; set; }
        public int LeadTimeDays { get; set; }
        public object? MinimumOrderQuantity { get; set; }
        public object? UnitPrice { get; set; }
        public string? Currency { get; set; }
        public bool IsPrimary { get; set; }
        public bool IsActive { get; set; }
        public int Version { get; set; }
        public string LastIdempotencyKey { get; set; } = "";
        public string LastRequestHash { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public SparePartSupplierRecord ToRecord() => new(
            PartSupplierId, PartId, VendorId, VendorPartNumber, LeadTimeDays,
            NullableDecimal(MinimumOrderQuantity), NullableDecimal(UnitPrice), Currency,
            IsPrimary, IsActive, Version,
            LastIdempotencyKey, LastRequestHash, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);
    }

    private sealed class BomRow
    {
        public string BomItemId { get; set; } = "";
        public string? EquipmentId { get; set; }
        public string? EquipmentClassId { get; set; }
        public string PartId { get; set; } = "";
        public object QuantityPer { get; set; } = 0m;
        public string? Criticality { get; set; }
        public int? ReplacementCycleDays { get; set; }
        public object? ReplacementCycleCount { get; set; }
        public string? PositionCode { get; set; }
        public bool IsActive { get; set; }
        public int Version { get; set; }
        public string LastIdempotencyKey { get; set; } = "";
        public string LastRequestHash { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; } = "";
        public DateTime UpdatedAt { get; set; }
        public EquipmentPartBomRecord ToRecord() => new(
            BomItemId, EquipmentId, EquipmentClassId, PartId, Decimal(QuantityPer), Criticality,
            ReplacementCycleDays, NullableDecimal(ReplacementCycleCount), PositionCode,
            IsActive, Version,
            LastIdempotencyKey, LastRequestHash, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);
    }

    private sealed class CommandRow
    {
        public string CommandId { get; set; } = "";
        public string EntityType { get; set; } = "";
        public string EntityId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public int ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public string ResultJson { get; set; } = "";
        public string ActorId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public SparePartMasterCommandRecord ToRecord() => new(
            CommandId, EntityType, EntityId, IdempotencyKey, RequestHash,
            ExpectedVersion, ResultVersion, ResultJson, ActorId, CreatedAt);
    }
}
