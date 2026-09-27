using NexaOne.Common;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>Moves stock into a new child LOT without creating or losing material.</summary>
public interface IMaterialLotSplitBridge : INexaModuleBridge
{
    Task<Result<MaterialLotSplitDto>> SplitAsync(
        MaterialLotSplitCommand command, CancellationToken ct = default);
}

public sealed record MaterialLotSplitCommand(
    string SplitId,
    string IdempotencyKey,
    string SourceSystem,
    string SourceEventId,
    string ParentLotId,
    string ChildLotId,
    int ExpectedParentVersion,
    decimal Quantity,
    DateTime OccurredAt,
    string? ChildLotNumber = null,
    string? ActorId = null);

public sealed record MaterialLotSplitDto(
    string SplitId,
    string ParentLotId,
    string ChildLotId,
    decimal Quantity,
    decimal ParentBalanceBefore,
    decimal ParentBalanceAfter,
    int ParentVersion,
    string ParentStatus,
    string ChildStatus,
    bool IsReplay);
