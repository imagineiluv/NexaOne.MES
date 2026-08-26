using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.SpareParts;

public sealed class SparePartBridge : ISparePartBridge
{
    private readonly SparePartService _service;
    public SparePartBridge(SparePartService service) => _service = service;

    public async Task<Result<SparePartStockPolicyDto>> SaveStockPolicyAsync(
        SparePartStockPolicyCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.SaveStockPolicyAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<SparePartStockPolicyDto>(result.Error);
    }

    public async Task<Result<SparePartSupplierDto>> SaveSupplierAsync(
        SparePartSupplierCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.SaveSupplierAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<SparePartSupplierDto>(result.Error);
    }

    public async Task<Result<EquipmentPartBomDto>> SaveEquipmentBomAsync(
        EquipmentPartBomCommand command,
        CancellationToken ct = default)
    {
        var result = await _service.SaveEquipmentBomAsync(command, ct);
        return result.IsSuccess
            ? Result.Success(ToDto(result.Value))
            : Result.Failure<EquipmentPartBomDto>(result.Error);
    }

    public Task<Result<SparePartReplenishmentDto>> RecommendReplenishmentAsync(
        string partId,
        CancellationToken ct = default)
        => _service.RecommendReplenishmentAsync(partId, ct);

    private static SparePartStockPolicyDto ToDto(SparePartStockPolicyRecord x) => new(
        x.PartId, x.SafetyStock, x.ReorderPoint, x.TargetStock, x.ReservedQuantity,
        x.AverageDailyUsage, x.ServiceLevel, x.ReviewCycleDays, x.IsActive,
        x.Version, x.UpdatedBy, x.UpdatedAt);

    private static SparePartSupplierDto ToDto(SparePartSupplierRecord x) => new(
        x.PartSupplierId, x.PartId, x.VendorId, x.VendorPartNumber, x.LeadTimeDays,
        x.MinimumOrderQuantity, x.UnitPrice, x.Currency, x.IsPrimary, x.IsActive,
        x.Version, x.UpdatedBy, x.UpdatedAt);

    private static EquipmentPartBomDto ToDto(EquipmentPartBomRecord x) => new(
        x.BomItemId, x.PartId, x.EquipmentId, x.EquipmentClassId, x.QuantityPer,
        x.Criticality, x.ReplacementCycleDays, x.ReplacementCycleCount, x.PositionCode,
        x.IsActive, x.Version, x.UpdatedBy, x.UpdatedAt);
}
