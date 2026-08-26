using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ems;

/// <summary>
/// 예비부품 기준정보와 보충 판단의 공통 seam이다. ExpectedVersion=0은 생성, 양수는 해당
/// 버전의 갱신을 뜻하며, 같은 IdempotencyKey는 동일 명령만 재생할 수 있다.
/// 재고 수불·실사용 원장은 하위 호환을 위해 <see cref="IEmsBridge"/>가 계속 소유한다.
/// </summary>
[NexaModuleBridge("Ems", "sparePartBridge")]
public interface ISparePartBridge : INexaModuleBridge
{
    Task<Result<SparePartStockPolicyDto>> SaveStockPolicyAsync(
        SparePartStockPolicyCommand command,
        CancellationToken ct = default);

    Task<Result<SparePartSupplierDto>> SaveSupplierAsync(
        SparePartSupplierCommand command,
        CancellationToken ct = default);

    Task<Result<EquipmentPartBomDto>> SaveEquipmentBomAsync(
        EquipmentPartBomCommand command,
        CancellationToken ct = default);

    Task<Result<SparePartReplenishmentDto>> RecommendReplenishmentAsync(
        string partId,
        CancellationToken ct = default);
}

public sealed record SparePartStockPolicyCommand(
    string PartId,
    decimal SafetyStock,
    decimal ReorderPoint,
    decimal TargetStock,
    decimal ReservedQuantity,
    decimal AverageDailyUsage,
    decimal? ServiceLevel,
    int? ReviewCycleDays,
    bool IsActive,
    int ExpectedVersion,
    string IdempotencyKey,
    string? ActorId = null);

public sealed record SparePartSupplierCommand(
    string PartSupplierId,
    string PartId,
    string VendorId,
    int LeadTimeDays,
    decimal? MinimumOrderQuantity,
    decimal? UnitPrice,
    string? Currency,
    bool IsPrimary,
    bool IsActive,
    int ExpectedVersion,
    string IdempotencyKey,
    string? VendorPartNumber = null,
    string? ActorId = null);

public sealed record EquipmentPartBomCommand(
    string BomItemId,
    string PartId,
    decimal QuantityPer,
    string? EquipmentId,
    string? EquipmentClassId,
    string? Criticality,
    int? ReplacementCycleDays,
    decimal? ReplacementCycleCount,
    string? PositionCode,
    bool IsActive,
    int ExpectedVersion,
    string IdempotencyKey,
    string? ActorId = null);

public sealed record SparePartStockPolicyDto(
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
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record SparePartSupplierDto(
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
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record EquipmentPartBomDto(
    string BomItemId,
    string PartId,
    string? EquipmentId,
    string? EquipmentClassId,
    decimal QuantityPer,
    string? Criticality,
    int? ReplacementCycleDays,
    decimal? ReplacementCycleCount,
    string? PositionCode,
    bool IsActive,
    int Version,
    string UpdatedBy,
    DateTime UpdatedAt);

public sealed record SparePartReplenishmentDto(
    string PartId,
    decimal CurrentStock,
    decimal ReservedQuantity,
    decimal AvailableQuantity,
    decimal SafetyStock,
    decimal ConfiguredReorderPoint,
    decimal LeadTimeDemand,
    decimal EffectiveReorderPoint,
    decimal ConfiguredTargetStock,
    decimal EffectiveTargetStock,
    decimal RecommendedOrderQuantity,
    bool ShouldOrder,
    string? PartSupplierId,
    string? VendorId,
    int? LeadTimeDays,
    decimal? MinimumOrderQuantity,
    string Reason);
