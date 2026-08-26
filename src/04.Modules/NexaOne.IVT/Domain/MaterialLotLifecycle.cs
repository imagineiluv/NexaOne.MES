namespace NexaOne.IVT.Domain;

public sealed record MaterialLotState(
    string LotId,
    string MaterialId,
    string? LotNumber,
    string? Location,
    decimal Balance,
    string Unit,
    string Status,
    int Version);

public sealed record MaterialLotTransaction(
    string TransactionId,
    string IdempotencyKey,
    string RequestHash,
    string Operation,
    string LotId,
    string MaterialId,
    decimal Quantity,
    decimal BalanceBefore,
    decimal BalanceAfter,
    decimal BalanceDelta,
    string? FromLocation,
    string? ToLocation,
    string PreviousStatus,
    string ResultStatus,
    int ExpectedVersion,
    int ResultVersion,
    DateTime OccurredAt,
    string ActorId,
    string SourceSystem,
    string SourceEventId,
    string? CorrelationId,
    string? Reason,
    string? MetadataJson,
    string? LotNumber = null,
    string? Unit = null,
    DateTime? ExpiryAt = null);
