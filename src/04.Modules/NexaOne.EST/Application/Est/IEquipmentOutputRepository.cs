namespace NexaOne.EST.Application.Est;

public interface IEquipmentOutputRepository
{
    Task<EquipmentOutputRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default);

    Task<EquipmentOutputRecord?> GetBySourceEventAsync(
        string source,
        string sourceEventId,
        CancellationToken ct = default);

    Task<bool> TryAddAsync(EquipmentOutputRecord record, CancellationToken ct = default);
}

public sealed record EquipmentOutputRecord(
    string OutputEventId,
    string IdempotencyKey,
    string RequestHash,
    string PlantId,
    string EquipmentId,
    string OutputType,
    string? CarrierId,
    string? ProcessLotId,
    string? WorkOrderId,
    string? ProcessId,
    string? RecipeId,
    int? RecipeVersion,
    decimal TotalQuantity,
    decimal GoodQuantity,
    decimal DefectQuantity,
    string Unit,
    string Source,
    string? SourceEventId,
    string ActorId,
    string? CorrelationId,
    string? MetadataJson,
    DateTime OccurredAt,
    DateTime CreatedAt,
    bool IsLotOutput = false);
