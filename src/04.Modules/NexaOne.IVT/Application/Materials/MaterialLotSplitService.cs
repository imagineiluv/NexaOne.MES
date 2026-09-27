using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

internal sealed record NormalizedMaterialLotSplit(
    string SplitId, string IdempotencyKey, string SourceSystem, string SourceEventId,
    string ParentLotId, string ChildLotId, int ExpectedParentVersion, decimal Quantity,
    DateTime OccurredAt, string ChildLotNumber, string ActorId, string RequestHash);

internal interface IMaterialLotSplitStore
{
    Task<Result<MaterialLotSplitDto>> TrySplitAsync(
        NormalizedMaterialLotSplit command, CancellationToken ct);
}

internal sealed class MaterialLotSplitService(IMaterialLotSplitStore store) : IMaterialLotSplitBridge
{
    private const decimal QuantityLimit = 10_000_000_000_000_000m;

    public Task<Result<MaterialLotSplitDto>> SplitAsync(
        MaterialLotSplitCommand command, CancellationToken ct = default)
    {
        if (command is null || !Valid(command.SplitId, 50)
            || !Valid(command.IdempotencyKey, 100)
            || !Valid(command.SourceSystem, 50)
            || !Valid(command.SourceEventId, 100)
            || !Valid(command.ParentLotId, 50)
            || !Valid(command.ChildLotId, 50)
            || !Valid(command.ActorId, 50)
            || command.ChildLotNumber?.Trim().Length > 100
            || command.ExpectedParentVersion <= 0
            || command.Quantity <= 0 || command.Quantity >= QuantityLimit
            || decimal.Round(command.Quantity, 6) != command.Quantity
            || command.OccurredAt == default)
            return Task.FromResult(Result.Failure<MaterialLotSplitDto>(Error.Validation(
                "IVT_SPLIT_INVALID", "LOT IDs, source, actor, version, time, and six-place positive quantity are required.")));

        var parentId = command.ParentLotId.Trim();
        var childId = command.ChildLotId.Trim();
        if (string.Equals(parentId, childId, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(Result.Failure<MaterialLotSplitDto>(Error.Validation(
                "IVT_SPLIT_SAME_LOT", "Parent and child LOT IDs must differ.")));

        var occurredAt = command.OccurredAt.Kind switch
        {
            DateTimeKind.Utc => command.OccurredAt,
            DateTimeKind.Local => command.OccurredAt.ToUniversalTime(),
            _ => DateTime.SpecifyKind(command.OccurredAt, DateTimeKind.Utc),
        };
        var lotNumber = string.IsNullOrWhiteSpace(command.ChildLotNumber)
            ? childId : command.ChildLotNumber.Trim();
        var input = new NormalizedMaterialLotSplit(
            command.SplitId.Trim(), command.IdempotencyKey.Trim(),
            command.SourceSystem.Trim(), command.SourceEventId.Trim(),
            parentId, childId, command.ExpectedParentVersion, command.Quantity,
            occurredAt, lotNumber, command.ActorId!.Trim(),
            CanonicalRequestHash.Compute(
                command.SplitId.Trim(), command.IdempotencyKey.Trim(),
                command.SourceSystem.Trim(), command.SourceEventId.Trim(),
                parentId, childId, command.ExpectedParentVersion, command.Quantity,
                occurredAt, lotNumber, command.ActorId.Trim()));
        return store.TrySplitAsync(input, ct);
    }

    private static bool Valid(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= maximumLength;
}
