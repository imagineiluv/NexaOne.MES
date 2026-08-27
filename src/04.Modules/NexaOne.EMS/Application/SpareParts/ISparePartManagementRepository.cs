namespace NexaOne.EMS.Application.SpareParts;

public interface ISparePartManagementRepository
{
    Task<bool> PartExistsAsync(string partId, CancellationToken ct = default);
    Task<SparePartMasterCommandRecord?> GetCommandAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task<SparePartStockPolicyRecord?> GetStockPolicyAsync(string partId, CancellationToken ct = default);
    Task<SparePartStockPolicyRecord?> GetStockPolicyByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> TryCreateStockPolicyAsync(SparePartStockPolicyRecord record, CancellationToken ct = default);
    Task<bool> TryUpdateStockPolicyAsync(SparePartStockPolicyRecord record, int expectedVersion, CancellationToken ct = default);
    Task<bool> TryCreateStockPolicyAsync(
        SparePartStockPolicyRecord record,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default);
    Task<bool> TryUpdateStockPolicyAsync(
        SparePartStockPolicyRecord record,
        int expectedVersion,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default);

    Task<SparePartSupplierRecord?> GetSupplierAsync(string partSupplierId, CancellationToken ct = default);
    Task<SparePartSupplierRecord?> GetSupplierByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> HasOtherActivePrimarySupplierAsync(
        string partId,
        string partSupplierId,
        CancellationToken ct = default);
    Task<bool> TryCreateSupplierAsync(SparePartSupplierRecord record, CancellationToken ct = default);
    Task<bool> TryUpdateSupplierAsync(SparePartSupplierRecord record, int expectedVersion, CancellationToken ct = default);
    Task<bool> TryCreateSupplierAsync(
        SparePartSupplierRecord record,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default);
    Task<bool> TryUpdateSupplierAsync(
        SparePartSupplierRecord record,
        int expectedVersion,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default);

    Task<EquipmentPartBomRecord?> GetEquipmentBomAsync(string bomItemId, CancellationToken ct = default);
    Task<EquipmentPartBomRecord?> GetEquipmentBomByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> TryCreateEquipmentBomAsync(EquipmentPartBomRecord record, CancellationToken ct = default);
    Task<bool> TryUpdateEquipmentBomAsync(EquipmentPartBomRecord record, int expectedVersion, CancellationToken ct = default);
    Task<bool> TryCreateEquipmentBomAsync(
        EquipmentPartBomRecord record,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default);
    Task<bool> TryUpdateEquipmentBomAsync(
        EquipmentPartBomRecord record,
        int expectedVersion,
        SparePartMasterCommandRecord command,
        CancellationToken ct = default);

    Task<SparePartReplenishmentInput?> GetReplenishmentInputAsync(
        string partId,
        CancellationToken ct = default);
}

public sealed record SparePartStockPolicyRecord(
    string PartId,
    decimal SafetyStock,
    decimal ReorderPoint,
    decimal TargetStock,
    decimal ReservedQuantity,
    decimal AverageDailyUsage,
    decimal? ServiceLevel,
    int? ReviewCycleDays,
    bool IsActive,
    int Version,
    string LastIdempotencyKey,
    string LastRequestHash,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record SparePartSupplierRecord(
    string PartSupplierId,
    string PartId,
    string VendorId,
    string? VendorPartNumber,
    int LeadTimeDays,
    decimal? MinimumOrderQuantity,
    decimal? UnitPrice,
    string? Currency,
    bool IsPrimary,
    bool IsActive,
    int Version,
    string LastIdempotencyKey,
    string LastRequestHash,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record EquipmentPartBomRecord(
    string BomItemId,
    string? EquipmentId,
    string? EquipmentClassId,
    string PartId,
    decimal QuantityPer,
    string? Criticality,
    int? ReplacementCycleDays,
    decimal? ReplacementCycleCount,
    string? PositionCode,
    bool IsActive,
    int Version,
    string LastIdempotencyKey,
    string LastRequestHash,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record SparePartReplenishmentInput(
    string PartId,
    decimal CurrentStock,
    SparePartStockPolicyRecord Policy,
    IReadOnlyList<SparePartSupplierRecord> Suppliers);

public sealed record SparePartMasterCommandRecord(
    string CommandId,
    string EntityType,
    string EntityId,
    string IdempotencyKey,
    string RequestHash,
    int ExpectedVersion,
    int ResultVersion,
    string ResultJson,
    string ActorId,
    DateTime CreatedAt);
