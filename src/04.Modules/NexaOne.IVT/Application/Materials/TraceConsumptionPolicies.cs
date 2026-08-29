using NexaOne.Common;
using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Application.Materials;

internal enum TraceConsumptionMode
{
    Direct,
    Pulse,
    CounterDelta,
    RateIntegrate,
}

/// <summary>
/// Internal calculation seam. Stock validation, idempotency and ledger persistence remain in
/// <see cref="ConsumptionService"/>; policies calculate only a non-negative quantity.
/// </summary>
internal interface ITraceConsumptionPolicy
{
    TraceConsumptionMode Mode { get; }

    Result<TraceConsumptionDecision> Evaluate(
        TraceProjectionItem item,
        TraceProjectionState? state);
}

internal sealed class TraceConsumptionPolicyCatalog
{
    private readonly IReadOnlyDictionary<TraceConsumptionMode, ITraceConsumptionPolicy> _policies;

    public TraceConsumptionPolicyCatalog()
        : this(new ITraceConsumptionPolicy[]
        {
            new DirectTraceConsumptionPolicy(),
            new PulseTraceConsumptionPolicy(),
            new CounterDeltaTraceConsumptionPolicy(),
            new RateIntegrateTraceConsumptionPolicy(),
        })
    {
    }

    internal TraceConsumptionPolicyCatalog(IEnumerable<ITraceConsumptionPolicy> policies)
    {
        _policies = policies.ToDictionary(policy => policy.Mode);
    }

    public Result<TraceConsumptionDecision> Evaluate(
        TraceProjectionItem item,
        TraceProjectionState? state)
    {
        if (!Enum.TryParse<TraceConsumptionMode>(item.CalculationMode, true, out var mode) ||
            !_policies.TryGetValue(mode, out var policy))
            return Result.Failure<TraceConsumptionDecision>(Error.Validation(
                nameof(item.CalculationMode),
                "Consumption mode must be Direct, Pulse, CounterDelta, or RateIntegrate."));

        if (item.ScaleFactor <= 0)
            return Result.Failure<TraceConsumptionDecision>(Error.Validation(
                nameof(item.ScaleFactor), "Scale factor must be greater than zero."));

        if (state is not null && item.CollectedAt <= state.LastCollectedAt)
            return Result.Success(TraceConsumptionDecision.Ignore("StaleOrDuplicateTimestamp"));

        return policy.Evaluate(item, state);
    }
}

internal sealed class DirectTraceConsumptionPolicy : ITraceConsumptionPolicy
{
    public TraceConsumptionMode Mode => TraceConsumptionMode.Direct;

    public Result<TraceConsumptionDecision> Evaluate(
        TraceProjectionItem item,
        TraceProjectionState? state)
    {
        if (item.RawValue < 0)
            return Result.Failure<TraceConsumptionDecision>(Error.Validation(
                nameof(item.RawValue), "Direct consumption value cannot be negative."));

        return Result.Success(new TraceConsumptionDecision(
            item.RawValue * item.ScaleFactor,
            AdvanceState: true,
            item.RawValue == 0 ? "ZeroDirectValue" : "Applied"));
    }
}

internal sealed class PulseTraceConsumptionPolicy : ITraceConsumptionPolicy
{
    public TraceConsumptionMode Mode => TraceConsumptionMode.Pulse;

    public Result<TraceConsumptionDecision> Evaluate(
        TraceProjectionItem item,
        TraceProjectionState? state)
    {
        if (item.PulseQuantity is null or <= 0)
            return Result.Failure<TraceConsumptionDecision>(Error.Validation(
                nameof(item.PulseQuantity), "Pulse mode requires a positive pulse quantity."));

        var risingEdge = item.RawValue > 0 && (state is null || state.LastValue <= 0);
        return Result.Success(new TraceConsumptionDecision(
            risingEdge ? item.PulseQuantity.Value * item.ScaleFactor : 0m,
            AdvanceState: true,
            risingEdge ? "Applied" : "NoRisingEdge"));
    }
}

internal sealed class CounterDeltaTraceConsumptionPolicy : ITraceConsumptionPolicy
{
    public TraceConsumptionMode Mode => TraceConsumptionMode.CounterDelta;

    public Result<TraceConsumptionDecision> Evaluate(
        TraceProjectionItem item,
        TraceProjectionState? state)
    {
        if (item.RawValue < 0)
            return Result.Failure<TraceConsumptionDecision>(Error.Validation(
                nameof(item.RawValue), "Counter value cannot be negative."));
        if (state is null)
            return Result.Success(TraceConsumptionDecision.Baseline());
        if (item.RawValue < state.LastValue)
            return Result.Success(TraceConsumptionDecision.Baseline("CounterReset"));

        var quantity = (item.RawValue - state.LastValue) * item.ScaleFactor;
        return Result.Success(new TraceConsumptionDecision(
            quantity,
            AdvanceState: true,
            quantity == 0 ? "NoCounterDelta" : "Applied"));
    }
}

internal sealed class RateIntegrateTraceConsumptionPolicy : ITraceConsumptionPolicy
{
    public TraceConsumptionMode Mode => TraceConsumptionMode.RateIntegrate;

    public Result<TraceConsumptionDecision> Evaluate(
        TraceProjectionItem item,
        TraceProjectionState? state)
    {
        if (item.RawValue < 0)
            return Result.Failure<TraceConsumptionDecision>(Error.Validation(
                nameof(item.RawValue), "Rate value cannot be negative."));
        if (state is null)
            return Result.Success(TraceConsumptionDecision.Baseline());

        var elapsedHours = (decimal)(item.CollectedAt - state.LastCollectedAt).TotalHours;
        if (elapsedHours <= 0)
            return Result.Success(TraceConsumptionDecision.Ignore("StaleOrDuplicateTimestamp"));

        // Trapezoidal integration. SCALE_FACTOR converts the configured rate/time base to OUTPUT_UNIT.
        var quantity = ((state.LastValue + item.RawValue) / 2m) * elapsedHours * item.ScaleFactor;
        return Result.Success(new TraceConsumptionDecision(
            quantity,
            AdvanceState: true,
            quantity == 0 ? "ZeroIntegratedRate" : "Applied"));
    }
}
