using NexaOne.Common;
using NexaOne.EMS.Domain;

namespace NexaOne.EMS.Application.Ems;

public sealed class MaintenancePlanService
{
    private readonly IMaintenancePlanRepository _planRepo;
    private readonly ISparePartRepository _partRepo;

    public MaintenancePlanService(IMaintenancePlanRepository planRepo, ISparePartRepository partRepo)
    {
        _planRepo = planRepo;
        _partRepo = partRepo;
    }

    // ── Maintenance Plans ─────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MaintenancePlan>> GetByEquipmentAsync(
        string equipmentId, CancellationToken ct = default)
        => await _planRepo.GetByEquipmentAsync(equipmentId, ct);

    public async Task<IReadOnlyList<MaintenancePlan>> GetByStatusAsync(
        MaintenancePlanStatus status, CancellationToken ct = default)
        => await _planRepo.GetByStatusAsync(status, ct);

    public async Task<Result<MaintenancePlan>> CreatePlanAsync(
        string planId, string planName, string equipmentId, string planType,
        string cycleType, DateTime scheduledDate, decimal estimatedHours, string assigneeId,
        CancellationToken ct = default)
    {
        var result = MaintenancePlan.Create(planId, planName, equipmentId, planType,
            cycleType, scheduledDate, estimatedHours, assigneeId);
        if (result.IsFailure) return result;
        await _planRepo.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> StartPlanAsync(string planId, CancellationToken ct = default)
    {
        var plan = await _planRepo.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFound(nameof(MaintenancePlan), planId));
        var r = plan.Start();
        if (r.IsFailure) return r;
        await _planRepo.UpdateAsync(plan, ct);
        return Result.Success();
    }

    public async Task<Result> CompletePlanAsync(string planId, CancellationToken ct = default)
    {
        var plan = await _planRepo.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFound(nameof(MaintenancePlan), planId));
        var r = plan.Complete();
        if (r.IsFailure) return r;
        await _planRepo.UpdateAsync(plan, ct);
        return Result.Success();
    }

    public async Task<Result> CancelPlanAsync(string planId, CancellationToken ct = default)
    {
        var plan = await _planRepo.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFound(nameof(MaintenancePlan), planId));
        var r = plan.Cancel();
        if (r.IsFailure) return r;
        await _planRepo.UpdateAsync(plan, ct);
        return Result.Success();
    }

    // ── Spare Parts ───────────────────────────────────────────────────────────

    public Task<IReadOnlyList<SparePart>> GetPartsAsync(CancellationToken ct = default)
        => _partRepo.GetAllAsync(ct);

    public Task<IReadOnlyList<SparePart>> GetLowStockPartsAsync(CancellationToken ct = default)
        => _partRepo.GetLowStockAsync(ct);

    public async Task<Result<SparePart>> CreatePartAsync(
        string partId, string partName, string partNumber, string description,
        string unitOfMeasure, decimal currentStock, decimal minStock, decimal maxStock,
        string location, string? equipmentClassId = null,
        CancellationToken ct = default)
    {
        var result = SparePart.Create(partId, partName, partNumber, description,
            unitOfMeasure, currentStock, minStock, maxStock, location, equipmentClassId);
        if (result.IsFailure) return result;
        await _partRepo.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> AdjustStockAsync(string partId, decimal delta, CancellationToken ct = default)
    {
        var part = await _partRepo.GetByIdAsync(partId, ct);
        if (part is null)
            return Result.Failure(Error.NotFound(nameof(SparePart), partId));
        var r = part.AdjustStock(delta);
        if (r.IsFailure) return r;
        await _partRepo.UpdateAsync(part, ct);
        return Result.Success();
    }
}
