using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

/// <summary>
/// IVT_MATERIAL_LOT와 append-only IVT_MATERIAL_TX를 함께 변경하는 자재 LOT 생명주기 모듈이다.
/// 상태 전이와 수량 계산은 이 경계 안에 두고 저장소에는 이미 결정된 원자적 이벤트만 전달한다.
/// </summary>
public sealed class MaterialLotService
{
    private const decimal QuantityLimit = 10_000_000_000_000_000m;
    private readonly IMaterialLotRepository _repository;

    public MaterialLotService(IMaterialLotRepository repository)
        => _repository = repository ?? throw new ArgumentNullException(nameof(repository));

    public async Task<Result<MaterialLotEventDto>> ExecuteAsync(
        MaterialLotCommand command,
        CancellationToken ct = default)
    {
        var normalized = Normalize(command);
        if (normalized.IsFailure) return Result.Failure<MaterialLotEventDto>(normalized.Error);
        var input = normalized.Value;

        var replay = await _repository.GetByIdempotencyKeyAsync(input.IdempotencyKey, ct);
        if (replay is not null) return Replay(input.RequestHash, replay);

        var sourceReplay = await _repository.GetBySourceEventAsync(
            input.SourceSystem, input.SourceEventId, ct);
        if (sourceReplay is not null) return SourceConflict(input, sourceReplay);

        MaterialLotTransaction record;
        if (input.Operation == MaterialLotOperations.Receive)
        {
            if (await _repository.GetLotAsync(input.MaterialLotId, ct) is not null)
                return Result.Failure<MaterialLotEventDto>(Error.Conflict(
                    $"Material lot '{input.MaterialLotId}' already exists."));

            record = BuildReceive(input);
            if (await _repository.TryReceiveAsync(record, ct))
                return Result.Success(ToDto(record, false));
        }
        else
        {
            var lot = await _repository.GetLotAsync(input.MaterialLotId, ct);
            if (lot is null)
                return Result.Failure<MaterialLotEventDto>(
                    Error.NotFoundOf(nameof(MaterialLotState), input.MaterialLotId));
            if (string.IsNullOrWhiteSpace(lot.MaterialId) || string.IsNullOrWhiteSpace(lot.Unit))
                return Result.Failure<MaterialLotEventDto>(Error.Conflict(
                    $"Material lot '{lot.LotId}' has incomplete material/unit master data and must be repaired before use."));
            if (lot.Version != input.ExpectedVersion)
                return VersionConflict(input.MaterialLotId, input.ExpectedVersion, lot.Version);
            if (input.MaterialId is not null &&
                !string.Equals(input.MaterialId, lot.MaterialId, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<MaterialLotEventDto>(Error.Conflict(
                    $"Material lot '{lot.LotId}' belongs to material '{lot.MaterialId}', not '{input.MaterialId}'."));

            var transition = BuildTransition(input, lot);
            if (transition.IsFailure)
                return Result.Failure<MaterialLotEventDto>(transition.Error);
            record = transition.Value;
            if (await _repository.TryApplyAsync(record, ct))
                return Result.Success(ToDto(record, false));
        }

        replay = await _repository.GetByIdempotencyKeyAsync(input.IdempotencyKey, ct);
        if (replay is not null) return Replay(input.RequestHash, replay);
        sourceReplay = await _repository.GetBySourceEventAsync(input.SourceSystem, input.SourceEventId, ct);
        if (sourceReplay is not null) return SourceConflict(input, sourceReplay);

        if (input.Operation != MaterialLotOperations.Receive
            && await _repository.HasFeedSessionReservationAsync(input.MaterialLotId, ct))
        {
            return Result.Failure<MaterialLotEventDto>(Error.Conflict(
                "IVT.MaterialLot.MountedConflict",
                $"Material lot '{input.MaterialLotId}' is reserved by a feed session. "
                + "Move/Hold/Release/Scrap/Adjustment remains blocked through Unmount PendingDrain; "
                + "consumption uses its separate ledger path."));
        }

        return Result.Failure<MaterialLotEventDto>(Error.Conflict(
            $"Material lot '{input.MaterialLotId}' changed concurrently; reload it before retrying."));
    }

    private static Result<NormalizedCommand> Normalize(MaterialLotCommand command)
    {
        if (command is null)
            return Result.Failure<NormalizedCommand>(Error.Validation(nameof(command), "Command is required."));

        var transactionId = Required(command.TransactionId, nameof(command.TransactionId), 50);
        if (transactionId.IsFailure) return Result.Failure<NormalizedCommand>(transactionId.Error);
        var key = Required(command.IdempotencyKey, nameof(command.IdempotencyKey), 100);
        if (key.IsFailure) return Result.Failure<NormalizedCommand>(key.Error);
        var lotId = Required(command.MaterialLotId, nameof(command.MaterialLotId), 50);
        if (lotId.IsFailure) return Result.Failure<NormalizedCommand>(lotId.Error);
        var sourceSystem = Required(command.SourceSystem, nameof(command.SourceSystem), 50);
        if (sourceSystem.IsFailure) return Result.Failure<NormalizedCommand>(sourceSystem.Error);
        var sourceEvent = Required(command.SourceEventId, nameof(command.SourceEventId), 100);
        if (sourceEvent.IsFailure) return Result.Failure<NormalizedCommand>(sourceEvent.Error);
        var operation = NormalizeOperation(command.Operation);
        if (operation is null)
            return Result.Failure<NormalizedCommand>(Error.Validation(
                nameof(command.Operation), "Operation must be Receive, Move, Hold, Release, Scrap, or Adjustment."));
        if (command.ExpectedVersion < 0 || (operation == MaterialLotOperations.Receive && command.ExpectedVersion != 0) ||
            (operation != MaterialLotOperations.Receive && command.ExpectedVersion == 0))
            return Result.Failure<NormalizedCommand>(Error.Validation(
                nameof(command.ExpectedVersion), "Receive requires version 0; existing-lot operations require a positive version."));

        var actor = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actor.IsFailure) return Result.Failure<NormalizedCommand>(actor.Error);
        var quantityResult = NormalizeQuantity(command.Quantity);
        if (quantityResult.IsFailure) return Result.Failure<NormalizedCommand>(quantityResult.Error);
        var quantity = quantityResult.Value;
        var materialId = Clean(command.MaterialId);
        var lotNumber = Clean(command.LotNumber);
        var unit = Clean(command.Unit);
        var location = Clean(command.Location);
        var reason = Clean(command.Reason);
        var correlation = Clean(command.CorrelationId);
        var metadata = Clean(command.MetadataJson);

        if (operation == MaterialLotOperations.Receive)
        {
            if (materialId is null || materialId.Length > 50)
                return RequiredFailure(nameof(command.MaterialId), 50);
            if (unit is null || unit.Length > 50)
                return RequiredFailure(nameof(command.Unit), 50);
            if (location is null || location.Length > 100)
                return RequiredFailure(nameof(command.Location), 100);
            if (quantity is null or <= 0)
                return QuantityFailure("Receive quantity must be greater than zero.");
        }
        if (operation == MaterialLotOperations.Move && (location is null || location.Length > 100))
            return RequiredFailure(nameof(command.Location), 100);
        if (operation == MaterialLotOperations.Scrap && quantity is null or <= 0)
            return QuantityFailure("Scrap quantity must be greater than zero.");
        if (operation == MaterialLotOperations.Adjustment && quantity is null or 0)
            return QuantityFailure("Adjustment quantity must be non-zero.");
        if (operation is MaterialLotOperations.Hold or MaterialLotOperations.Scrap or MaterialLotOperations.Adjustment &&
            (reason is null || reason.Length > 500))
            return RequiredFailure(nameof(command.Reason), 500);
        if (operation is MaterialLotOperations.Move or MaterialLotOperations.Hold or MaterialLotOperations.Release &&
            quantity is not null and not 0)
            return QuantityFailure($"{operation} does not accept a quantity.");
        if (materialId?.Length > 50 || lotNumber?.Length > 100 || unit?.Length > 50 ||
            reason?.Length > 500 || correlation?.Length > 100)
            return Result.Failure<NormalizedCommand>(Error.Validation("MaterialLotCommand", "One or more fields exceed their database length."));

        var occurredAt = Utc(command.OccurredAt);
        DateTime? expiryAt = command.ExpiryAt is null ? null : Utc(command.ExpiryAt.Value);
        var hash = CanonicalRequestHash.Compute(
            transactionId.Value, key.Value, operation, lotId.Value, command.ExpectedVersion,
            occurredAt, sourceSystem.Value, sourceEvent.Value, materialId, lotNumber, quantity,
            unit, location, reason, expiryAt, actor.Value, correlation, metadata);
        return Result.Success(new NormalizedCommand(
            transactionId.Value, key.Value, hash, operation, lotId.Value, command.ExpectedVersion,
            occurredAt, sourceSystem.Value, sourceEvent.Value, materialId, lotNumber, quantity,
            unit, location, reason, expiryAt, actor.Value, correlation, metadata));
    }

    private static MaterialLotTransaction BuildReceive(NormalizedCommand c) => new(
        c.TransactionId, c.IdempotencyKey, c.RequestHash, c.Operation, c.MaterialLotId,
        c.MaterialId!, c.Quantity!.Value, 0, c.Quantity.Value, c.Quantity.Value,
        null, c.Location, string.Empty, "InStock", 0, 1, c.OccurredAt, c.ActorId, c.SourceSystem,
        c.SourceEventId, c.CorrelationId, c.Reason, c.MetadataJson, c.LotNumber, c.Unit, c.ExpiryAt);

    private static Result<MaterialLotTransaction> BuildTransition(NormalizedCommand c, MaterialLotState lot)
    {
        if (!new[] { "InStock", "Hold", "Consumed", "Scrapped" }
                .Any(value => string.Equals(value, lot.Status, StringComparison.OrdinalIgnoreCase)))
            return Result.Failure<MaterialLotTransaction>(Error.Conflict(
                $"Material lot '{lot.LotId}' has unsupported status '{lot.Status}'."));
        if (string.Equals(lot.Status, "Scrapped", StringComparison.OrdinalIgnoreCase))
            return Result.Failure<MaterialLotTransaction>(Error.Conflict("A scrapped material lot is terminal."));

        var quantity = c.Quantity ?? 0m;
        var after = lot.Balance;
        var status = lot.Status;
        var toLocation = lot.Location;
        switch (c.Operation)
        {
            case MaterialLotOperations.Move:
                if (string.Equals(lot.Status, "Consumed", StringComparison.OrdinalIgnoreCase))
                    return InvalidState(c.Operation, lot.Status);
                if (string.Equals(lot.Location, c.Location, StringComparison.OrdinalIgnoreCase))
                    return Result.Failure<MaterialLotTransaction>(Error.Conflict("Destination must differ from the current location."));
                toLocation = c.Location;
                break;
            case MaterialLotOperations.Hold:
                if (!string.Equals(lot.Status, "InStock", StringComparison.OrdinalIgnoreCase))
                    return InvalidState(c.Operation, lot.Status);
                status = "Hold";
                break;
            case MaterialLotOperations.Release:
                if (!string.Equals(lot.Status, "Hold", StringComparison.OrdinalIgnoreCase))
                    return InvalidState(c.Operation, lot.Status);
                status = lot.Balance == 0 ? "Consumed" : "InStock";
                break;
            case MaterialLotOperations.Scrap:
                if (quantity > lot.Balance)
                    return Result.Failure<MaterialLotTransaction>(Error.Conflict("Scrap quantity exceeds the material lot balance."));
                after = lot.Balance - quantity;
                status = after == 0 ? "Scrapped" : lot.Status;
                break;
            case MaterialLotOperations.Adjustment:
                after = lot.Balance + quantity;
                if (after < 0)
                    return Result.Failure<MaterialLotTransaction>(Error.Conflict("Adjustment would make the material lot balance negative."));
                status = string.Equals(lot.Status, "Hold", StringComparison.OrdinalIgnoreCase)
                    ? "Hold"
                    : after == 0 ? "Consumed" : "InStock";
                break;
        }

        return Result.Success(new MaterialLotTransaction(
            c.TransactionId, c.IdempotencyKey, c.RequestHash, c.Operation, lot.LotId,
            lot.MaterialId, quantity, lot.Balance, after, after - lot.Balance,
            lot.Location, toLocation, lot.Status, status, lot.Version, lot.Version + 1, c.OccurredAt,
            c.ActorId, c.SourceSystem, c.SourceEventId, c.CorrelationId, c.Reason, c.MetadataJson));
    }

    private static Result<MaterialLotTransaction> InvalidState(string operation, string status)
        => Result.Failure<MaterialLotTransaction>(Error.Conflict(
            $"Operation '{operation}' is not allowed while the material lot is '{status}'."));

    private static Result<MaterialLotEventDto> Replay(string hash, MaterialLotTransaction replay)
        => string.Equals(hash, replay.RequestHash, StringComparison.Ordinal)
            ? Result.Success(ToDto(replay, true))
            : Result.Failure<MaterialLotEventDto>(Error.Conflict(
                $"Idempotency key '{replay.IdempotencyKey}' is already used for a different material-lot command."));

    private static Result<MaterialLotEventDto> SourceConflict(
        NormalizedCommand command, MaterialLotTransaction replay)
        => Result.Failure<MaterialLotEventDto>(Error.Conflict(
            $"Source event '{command.SourceSystem}/{command.SourceEventId}' was already recorded as transaction '{replay.TransactionId}'."));

    private static Result<MaterialLotEventDto> VersionConflict(string lotId, int expected, int actual)
        => Result.Failure<MaterialLotEventDto>(Error.Conflict(
            $"Material lot '{lotId}' version conflict: expected {expected}, actual {actual}."));

    private static MaterialLotEventDto ToDto(MaterialLotTransaction r, bool replay) => new(
        r.TransactionId, r.IdempotencyKey, r.Operation, r.LotId, r.MaterialId, r.Quantity,
        r.BalanceBefore, r.BalanceAfter, r.BalanceDelta, r.FromLocation, r.ToLocation,
        r.ResultStatus, r.ExpectedVersion, r.ResultVersion, r.OccurredAt, r.ActorId,
        r.SourceSystem, r.SourceEventId, r.CorrelationId, replay);

    private static string? NormalizeOperation(string? operation)
        => new[] { MaterialLotOperations.Receive, MaterialLotOperations.Move, MaterialLotOperations.Hold,
                MaterialLotOperations.Release, MaterialLotOperations.Scrap, MaterialLotOperations.Adjustment }
            .FirstOrDefault(value => string.Equals(value, operation?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static Result<string> Required(string? value, string name, int max)
    {
        var cleaned = Clean(value);
        return cleaned is null || cleaned.Length > max
            ? Result.Failure<string>(Error.Validation(name, $"{name} is required and cannot exceed {max} characters."))
            : Result.Success(cleaned);
    }

    private static Result<NormalizedCommand> RequiredFailure(string name, int max)
        => Result.Failure<NormalizedCommand>(Error.Validation(name, $"{name} is required and cannot exceed {max} characters."));

    private static Result<NormalizedCommand> QuantityFailure(string message)
        => Result.Failure<NormalizedCommand>(Error.Validation(nameof(MaterialLotCommand.Quantity), message));

    private static Result<decimal?> NormalizeQuantity(decimal? value)
    {
        if (value is null) return Result.Success<decimal?>(null);
        var rounded = decimal.Round(value.Value, 6, MidpointRounding.AwayFromZero);
        if (rounded <= -QuantityLimit || rounded >= QuantityLimit)
            return Result.Failure<decimal?>(Error.Validation(nameof(MaterialLotCommand.Quantity),
                "Quantity exceeds the DECIMAL(22,6) accounting range."));
        if (value != 0 && rounded == 0)
            return Result.Failure<decimal?>(Error.Validation(nameof(MaterialLotCommand.Quantity),
                "Quantity magnitude must be at least 0.000001."));
        return Result.Success<decimal?>(rounded);
    }

    private static DateTime Utc(DateTime value) => value == default
        ? DateTime.UtcNow
        : value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        };
    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record NormalizedCommand(
        string TransactionId, string IdempotencyKey, string RequestHash, string Operation,
        string MaterialLotId, int ExpectedVersion, DateTime OccurredAt, string SourceSystem,
        string SourceEventId, string? MaterialId, string? LotNumber, decimal? Quantity,
        string? Unit, string? Location, string? Reason, DateTime? ExpiryAt, string ActorId,
        string? CorrelationId, string? MetadataJson);
}
