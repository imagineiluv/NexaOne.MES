namespace NexaOne.IVT.Domain;

/// <summary>IVT 소비 바인딩과 영속 TRACE 원천의 재시작 커서를 묶은 읽기 범위다.</summary>
public sealed record TraceProjectionBinding(
    string BindingId,
    string PlantId,
    string EquipmentId,
    string ParameterId,
    string FeedPointId,
    string CalculationMode,
    decimal ScaleFactor,
    decimal? PulseQuantity,
    string OutputUnit,
    DateTime EffectiveFrom,
    DateTime? EffectiveTo,
    DateTime? LastEnqueuedAt,
    string? LastEnqueuedCollectId)
{
    public TraceProjectionItem Snapshot(TraceSourceObservation sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        if (!string.Equals(sample.ScopeId, BindingId, StringComparison.Ordinal)
            || !string.Equals(sample.EquipmentId, EquipmentId, StringComparison.Ordinal)
            || !string.Equals(sample.ParameterId, ParameterId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"FDC TRACE sample '{sample.CollectId}' does not belong to binding '{BindingId}'.");
        }

        return new TraceProjectionItem(
            BindingId,
            sample.CollectId,
            PlantId,
            EquipmentId,
            ParameterId,
            FeedPointId,
            CalculationMode,
            ScaleFactor,
            PulseQuantity,
            OutputUnit,
            sample.Value,
            sample.Quality,
            sample.CollectedAt);
    }
}

/// <summary>
/// IVT가 외부 TRACE 계약에서 받아들인 표본의 도메인 중립 형태다. FDC 계약 DTO는
/// Application 경계에서 이 값으로 변환되며 IVT Domain으로 전파되지 않는다.
/// </summary>
public sealed record TraceSourceObservation(
    string ScopeId,
    string CollectId,
    string EquipmentId,
    string ParameterId,
    decimal Value,
    string Quality,
    DateTime CollectedAt);

/// <summary>A persisted FDC sample with the effective binding configuration snapshotted in the inbox.</summary>
public sealed record TraceProjectionItem(
    string BindingId,
    string CollectId,
    string PlantId,
    string EquipmentId,
    string ParameterId,
    string FeedPointId,
    string CalculationMode,
    decimal ScaleFactor,
    decimal? PulseQuantity,
    string OutputUnit,
    decimal RawValue,
    string Quality,
    DateTime CollectedAt,
    string? LeaseOwnerId = null);

/// <summary>The last successfully advanced observation for a binding.</summary>
public sealed record TraceProjectionState(
    string BindingId,
    string LastCollectId,
    decimal LastValue,
    DateTime LastCollectedAt);

/// <summary>The material and optional production context physically mounted at a feed point.</summary>
public sealed record MaterialFeedSession(
    string FeedSessionId,
    string PlantId,
    string EquipmentId,
    string FeedPointId,
    string MaterialLotId,
    string MaterialId,
    string? ProcessLotId,
    string? WorkOrderId,
    string? ProcessId,
    string? RecipeId,
    int? RecipeVersion,
    string MountedBy,
    DateTime MountedAt,
    DateTime? UnmountedAt);

internal sealed record TraceConsumptionDecision(
    decimal Quantity,
    bool AdvanceState,
    string Disposition)
{
    public static TraceConsumptionDecision Baseline(string disposition = "Baseline") =>
        new(0m, true, disposition);

    public static TraceConsumptionDecision Ignore(string disposition) =>
        new(0m, false, disposition);
}
