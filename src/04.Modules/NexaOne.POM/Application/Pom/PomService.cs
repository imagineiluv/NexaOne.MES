using NexaOne.Common;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.Pom;

public sealed class PomService
{
    private readonly IProductionPlanRepository _planRepository;

    public PomService(IProductionPlanRepository planRepository)
    {
        _planRepository = planRepository;
    }

    public async Task<Result<IReadOnlyList<ProductionPlan>>> GetByPlantAsync(
        string plantId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var list = await _planRepository.GetByPlantAsync(plantId, from, to, ct);
        return Result.Success(list);
    }

    public Task<int> GetCountByStatusAsync(ProductionPlanStatus status, CancellationToken ct = default)
        => _planRepository.GetCountByStatusAsync(status.ToString(), ct);

    public async Task<Result<ProductionPlan>> CreatePlanAsync(
        string planId,
        string planName,
        string plantId,
        string productId,
        decimal qty,
        DateTime start,
        DateTime end,
        CancellationToken ct = default)
    {
        var result = ProductionPlan.Create(planId, planName, plantId, productId, qty, start, end);
        if (result.IsFailure) return result;

        await _planRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> StartPlanAsync(string planId, CancellationToken ct = default)
    {
        var plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFoundOf(nameof(ProductionPlan), planId));

        var startResult = plan.Start();
        if (startResult.IsFailure) return startResult;

        await _planRepository.UpdateAsync(plan, ct);
        return Result.Success();
    }

    public async Task<Result> ReleasePlanAsync(string planId, CancellationToken ct = default)
    {
        var plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFoundOf(nameof(ProductionPlan), planId));

        var releaseResult = plan.Release();
        if (releaseResult.IsFailure) return releaseResult;

        await _planRepository.UpdateAsync(plan, ct);
        return Result.Success();
    }

    public async Task<Result> CompletePlanAsync(string planId, CancellationToken ct = default)
    {
        var plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFoundOf(nameof(ProductionPlan), planId));

        var completeResult = plan.Complete();
        if (completeResult.IsFailure) return completeResult;

        await _planRepository.UpdateAsync(plan, ct);
        return Result.Success();
    }

    public async Task<Result> CancelPlanAsync(string planId, CancellationToken ct = default)
    {
        var plan = await _planRepository.GetByIdAsync(planId, ct);
        if (plan is null)
            return Result.Failure(Error.NotFoundOf(nameof(ProductionPlan), planId));

        var cancelResult = plan.Cancel();
        if (cancelResult.IsFailure) return cancelResult;

        await _planRepository.UpdateAsync(plan, ct);
        return Result.Success();
    }
}
