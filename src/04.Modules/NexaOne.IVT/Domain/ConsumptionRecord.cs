namespace NexaOne.IVT.Domain;

public sealed record MaterialLotBalance(
    string LotId,
    string MaterialId,
    decimal CurrentQuantity,
    string Unit,
    string Status);

public sealed record ConsumptionRecord(
    string ConsumptionId,
    string IdempotencyKey,
    string RequestHash,
    string PlantId,
    string EquipmentId,
    string MaterialLotId,
    string MaterialId,
    string? ProcessLotId,
    string? WorkOrderId,
    string? ProcessId,
    string? RecipeId,
    int? RecipeVersion,
    string Mode,
    decimal Quantity,
    string Unit,
    string? TraceId,
    string? TagId,
    string SourceEventId,
    string SourceSystem,
    string OperatorId,
    string? FeedSessionId,
    string? CorrelationId,
    string? ReversalOfId,
    string Status,
    string? MetadataJson,
    DateTime OccurredAt,
    string? WorkScopeId = null,
    string? CarrierId = null);
