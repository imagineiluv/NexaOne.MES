namespace NexaOne.IVT.Domain;

internal sealed record TraceBindingState(
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
    bool IsActive,
    int Version,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt);

internal sealed record TraceBindingWrite(
    string CommandId,
    string Operation,
    string IdempotencyKey,
    string RequestHash,
    TraceBindingState Result,
    int ExpectedVersion,
    string ActorId,
    DateTime OccurredAt,
    string SourceSystem,
    string SourceEventId,
    string? CorrelationId,
    string? Reason);

internal sealed record TraceBindingCursor(
    string LastCollectId,
    DateTime LastCollectedAt);

internal sealed record FeedSessionState(
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
    DateTime MountedAt,
    string MountedBy,
    DateTime? UnmountedAt,
    string? UnmountedBy,
    string Status,
    int Version,
    string CreatedBy,
    DateTime CreatedAt,
    string UpdatedBy,
    DateTime UpdatedAt);

internal sealed record FeedSessionWrite(
    string CommandId,
    string Operation,
    string IdempotencyKey,
    string RequestHash,
    FeedSessionState Result,
    int ExpectedVersion,
    string ActorId,
    DateTime OccurredAt,
    string SourceSystem,
    string SourceEventId,
    string? CorrelationId,
    string? Reason);
