namespace NexaOne.EMS.Application.SpareParts;

public interface ISparePartManagementRepository
{
    Task<bool> PartExistsAsync(string partId, CancellationToken ct = default);
    Task<bool> VendorExistsAsync(string vendorId, CancellationToken ct = default);
    Task<bool> EquipmentExistsAsync(string equipmentId, CancellationToken ct = default);
    Task<bool> EquipmentClassExistsAsync(string equipmentClassId, CancellationToken ct = default);

    Task<SparePartStockPolicyRecord?> GetStockPolicyAsync(string partId, CancellationToken ct = default);
    Task<SparePartStockPolicyRecord?> GetStockPolicyByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> TryCreateStockPolicyAsync(SparePartStockPolicyRecord record, CancellationToken ct = default);
    Task<bool> TryUpdateStockPolicyAsync(SparePartStockPolicyRecord record, int expectedVersion, CancellationToken ct = default);

    Task<SparePartSupplierRecord?> GetSupplierAsync(string partSupplierId, CancellationToken ct = default);
    Task<SparePartSupplierRecord?> GetSupplierByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> HasOtherActivePrimarySupplierAsync(
        string partId,
        string partSupplierId,
        CancellationToken ct = default);
    Task<bool> TryCreateSupplierAsync(SparePartSupplierRecord record, CancellationToken ct = default);
    Task<bool> TryUpdateSupplierAsync(SparePartSupplierRecord record, int expectedVersion, CancellationToken ct = default);

    Task<EquipmentPartBomRecord?> GetEquipmentBomAsync(string bomItemId, CancellationToken ct = default);
    Task<EquipmentPartBomRecord?> GetEquipmentBomByIdempotencyKeyAsync(string key, CancellationToken ct = default);
    Task<bool> TryCreateEquipmentBomAsync(EquipmentPartBomRecord record, CancellationToken ct = default);
    Task<bool> TryUpdateEquipmentBomAsync(EquipmentPartBomRecord record, int expectedVersion, CancellationToken ct = default);

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
