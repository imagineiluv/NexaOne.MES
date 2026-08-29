using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

/// <summary>
/// 자재 LOT의 물리 장착 세션을 관리한다. 자재 LOT의 정본 상태는 IMaterialLotRepository에서
/// 확인하고 세션 상태와 command ledger는 IFeedSessionRepository가 한 트랜잭션으로 기록한다.
/// </summary>
internal sealed class FeedSessionService
{
    private readonly IFeedSessionRepository _repository;
    private readonly IMaterialLotRepository _materialLots;

    public FeedSessionService(
        IFeedSessionRepository repository,
        IMaterialLotRepository materialLots)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _materialLots = materialLots ?? throw new ArgumentNullException(nameof(materialLots));
    }

    public async Task<Result<FeedSessionDto>> ExecuteAsync(
        FeedSessionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = NormalizeOperation(command.Operation);
        if (operation is null)
        {
            return Result.Failure<FeedSessionDto>(Error.Validation(
                nameof(command.Operation), "Operation must be Mount or Unmount."));
        }

        return operation == FeedSessionOperations.Mount
            ? await MountAsync(command, ct)
            : await CloseAsync(command, operation, ct);
    }

    private async Task<Result<FeedSessionDto>> MountAsync(
        FeedSessionCommand command,
        CancellationToken ct)
    {
        var normalized = NormalizeMount(command);
        if (normalized.IsFailure) return Result.Failure<FeedSessionDto>(normalized.Error);
        var request = normalized.Value;

        var replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        var sourceReplay = await _repository.GetBySourceEventAsync(
            request.SourceSystem, request.SourceEventId, ct);
        if (sourceReplay is not null) return SourceConflict(request.SourceSystem, request.SourceEventId, sourceReplay);

        var lot = await _materialLots.GetLotAsync(request.MaterialLotId, ct);
        if (lot is null)
            return Result.Failure<FeedSessionDto>(Error.NotFoundOf("MaterialLot", request.MaterialLotId));
        if (request.MaterialId is not null
            && !string.Equals(request.MaterialId, lot.MaterialId, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<FeedSessionDto>(Error.Conflict(
                "IVT.FeedSession.MaterialMismatch",
                $"Material lot '{lot.LotId}' belongs to material '{lot.MaterialId}', not '{request.MaterialId}'."));
        }
        if (string.IsNullOrWhiteSpace(lot.MaterialId)
            || !string.Equals(lot.Status, "InStock", StringComparison.OrdinalIgnoreCase)
            || lot.Balance <= 0m)
        {
            return Result.Failure<FeedSessionDto>(Error.Conflict(
                "IVT.FeedSession.MaterialUnavailable",
                $"Material lot '{lot.LotId}' must be InStock with a positive balance before mounting."));
        }

        var committedAt = DateTime.UtcNow;
        var session = new FeedSessionState(
            request.FeedSessionId,
            request.PlantId,
            request.EquipmentId,
            request.FeedPointId,
            request.MaterialLotId,
            lot.MaterialId,
            request.ProcessLotId,
            request.WorkOrderId,
            request.ProcessId,
            request.RecipeId,
            request.RecipeVersion,
            request.OccurredAt,
            request.ActorId,
            null,
            null,
            "Mounted",
            1,
            request.ActorId,
            committedAt,
            request.ActorId,
            committedAt);
        var write = new FeedSessionWrite(
            $"FSC_{Guid.NewGuid():N}",
            FeedSessionOperations.Mount,
            request.IdempotencyKey,
            request.RequestHash,
            session,
            0,
            request.ActorId,
            request.OccurredAt,
            request.SourceSystem,
            request.SourceEventId,
            request.CorrelationId,
            request.Reason);

        if (await _repository.TryMountAsync(session, write, ct))
            return Result.Success(ToDto(write, false));

        replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        sourceReplay = await _repository.GetBySourceEventAsync(
            request.SourceSystem, request.SourceEventId, ct);
        if (sourceReplay is not null) return SourceConflict(request.SourceSystem, request.SourceEventId, sourceReplay);

        return Result.Failure<FeedSessionDto>(Error.Conflict(
            "IVT.FeedSession.MountConflict",
            $"Feed session '{request.FeedSessionId}' already exists or feed point "
            + $"'{request.PlantId}/{request.EquipmentId}/{request.FeedPointId}' is already mounted."));
    }

    private async Task<Result<FeedSessionDto>> CloseAsync(
        FeedSessionCommand command,
        string operation,
        CancellationToken ct)
    {
        var normalized = NormalizeClose(command, operation);
        if (normalized.IsFailure) return Result.Failure<FeedSessionDto>(normalized.Error);
        var request = normalized.Value;

        var replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        var sourceReplay = await _repository.GetBySourceEventAsync(
            request.SourceSystem, request.SourceEventId, ct);
        if (sourceReplay is not null) return SourceConflict(request.SourceSystem, request.SourceEventId, sourceReplay);

        var current = await _repository.GetAsync(request.FeedSessionId, ct);
        if (current is null)
            return Result.Failure<FeedSessionDto>(Error.NotFoundOf("FeedSession", request.FeedSessionId));
        if (current.Version != request.ExpectedVersion)
            return VersionConflict(request.FeedSessionId, request.ExpectedVersion, current.Version);
        if (!string.Equals(current.Status, "Mounted", StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<FeedSessionDto>(Error.Conflict(
                "IVT.FeedSession.AlreadyClosed",
                $"Feed session '{request.FeedSessionId}' is already {current.Status} at version {current.Version}."));
        }
        if (request.OccurredAt <= current.MountedAt)
        {
            return Result.Failure<FeedSessionDto>(Error.Validation(
                nameof(command.OccurredAt), "Unmount time must be after MountedAt."));
        }

        var committedAt = DateTime.UtcNow;
        var closed = current with
        {
            UnmountedAt = request.OccurredAt,
            UnmountedBy = request.ActorId,
            Status = "Unmounted",
            Version = current.Version + 1,
            UpdatedBy = request.ActorId,
            UpdatedAt = committedAt,
        };
        var write = new FeedSessionWrite(
            $"FSC_{Guid.NewGuid():N}",
            operation,
            request.IdempotencyKey,
            request.RequestHash,
            closed,
            request.ExpectedVersion,
            request.ActorId,
            request.OccurredAt,
            request.SourceSystem,
            request.SourceEventId,
            request.CorrelationId,
            request.Reason);

        if (await _repository.TryCloseAsync(closed, request.ExpectedVersion, write, ct))
            return Result.Success(ToDto(write, false));

        replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        sourceReplay = await _repository.GetBySourceEventAsync(
            request.SourceSystem, request.SourceEventId, ct);
        if (sourceReplay is not null) return SourceConflict(request.SourceSystem, request.SourceEventId, sourceReplay);
        current = await _repository.GetAsync(request.FeedSessionId, ct);
        return VersionConflict(request.FeedSessionId, request.ExpectedVersion, current?.Version);
    }

    private static Result<NormalizedMount> NormalizeMount(FeedSessionCommand command)
    {
        var sessionId = Required(command.FeedSessionId, 50);
        var plantId = Required(command.PlantId, 50);
        var equipmentId = Required(command.EquipmentId, 50);
        var feedPointId = Required(command.FeedPointId, 50);
        var materialLotId = Required(command.MaterialLotId, 50);
        var idempotencyKey = Required(command.IdempotencyKey, 100);
        var sourceSystem = Required(command.SourceSystem, 50);
        var sourceEventId = Required(command.SourceEventId, 100);
        if (sessionId is null || plantId is null || equipmentId is null || feedPointId is null
            || materialLotId is null || idempotencyKey is null || sourceSystem is null
            || sourceEventId is null)
        {
            return Result.Failure<NormalizedMount>(Error.Validation(
                "IVT.FeedSession.Required",
                "Session, plant, equipment, feed point, material lot, idempotency and source identity are required and must fit storage limits."));
        }
        if (command.ExpectedVersion != 0)
            return Result.Failure<NormalizedMount>(Error.Validation(
                nameof(command.ExpectedVersion), "Mount requires ExpectedVersion=0."));
        if (command.OccurredAt == default)
            return Result.Failure<NormalizedMount>(Error.Validation(
                nameof(command.OccurredAt), "OccurredAt is required."));
        var occurredAt = Utc(command.OccurredAt);
        if (occurredAt > DateTime.UtcNow)
            return Result.Failure<NormalizedMount>(Error.Validation(
                nameof(command.OccurredAt), "A physical mount cannot occur in the future."));
        if (command.RecipeVersion is <= 0)
            return Result.Failure<NormalizedMount>(Error.Validation(
                nameof(command.RecipeVersion), "RecipeVersion must be positive when supplied."));

        var materialId = Optional(command.MaterialId, 50);
        var processLotId = Optional(command.ProcessLotId, 50);
        var workOrderId = Optional(command.WorkOrderId, 50);
        var processId = Optional(command.ProcessId, 50);
        var recipeId = Optional(command.RecipeId, 50);
        var correlationId = Optional(command.CorrelationId, 100);
        var reason = Optional(command.Reason, 500);
        if (new[] { materialId, processLotId, workOrderId, processId, recipeId, correlationId, reason }
            .Any(value => value.Invalid))
        {
            return Result.Failure<NormalizedMount>(Error.Validation(
                "IVT.FeedSession.OptionalLength", "One or more optional fields exceed storage limits."));
        }

        var actor = CommandActor.Resolve(command.ActorId);
        if (actor.IsFailure) return Result.Failure<NormalizedMount>(actor.Error);
        var requestHash = CanonicalRequestHash.Compute(
            FeedSessionOperations.Mount,
            sessionId,
            0,
            plantId,
            equipmentId,
            feedPointId,
            materialLotId,
            materialId.Value,
            processLotId.Value,
            workOrderId.Value,
            processId.Value,
            recipeId.Value,
            command.RecipeVersion,
            actor.Value,
            occurredAt,
            sourceSystem,
            sourceEventId,
            correlationId.Value,
            reason.Value);
        return Result.Success(new NormalizedMount(
            sessionId, plantId, equipmentId, feedPointId, materialLotId, materialId.Value,
            processLotId.Value, workOrderId.Value, processId.Value, recipeId.Value,
            command.RecipeVersion, idempotencyKey, requestHash, actor.Value, occurredAt,
            sourceSystem, sourceEventId, correlationId.Value, reason.Value));
    }

    private static Result<NormalizedClose> NormalizeClose(
        FeedSessionCommand command,
        string operation)
    {
        var sessionId = Required(command.FeedSessionId, 50);
        var idempotencyKey = Required(command.IdempotencyKey, 100);
        var sourceSystem = Required(command.SourceSystem, 50);
        var sourceEventId = Required(command.SourceEventId, 100);
        if (sessionId is null || idempotencyKey is null || sourceSystem is null || sourceEventId is null)
        {
            return Result.Failure<NormalizedClose>(Error.Validation(
                "IVT.FeedSession.Required",
                "Session, idempotency and source identity are required and must fit storage limits."));
        }
        if (command.ExpectedVersion < 1)
            return Result.Failure<NormalizedClose>(Error.Validation(
                nameof(command.ExpectedVersion), "Unmount requires a positive ExpectedVersion."));
        if (command.OccurredAt == default)
            return Result.Failure<NormalizedClose>(Error.Validation(
                nameof(command.OccurredAt), "OccurredAt is required."));
        var occurredAt = Utc(command.OccurredAt);
        if (occurredAt > DateTime.UtcNow)
            return Result.Failure<NormalizedClose>(Error.Validation(
                nameof(command.OccurredAt), "A physical unmount cannot occur in the future."));

        var correlationId = Optional(command.CorrelationId, 100);
        var reason = Required(command.Reason, 500);
        if (correlationId.Invalid || reason is null)
            return Result.Failure<NormalizedClose>(Error.Validation(
                "IVT.FeedSession.CloseReason", "Unmount requires a reason up to 500 characters."));
        var actor = CommandActor.Resolve(command.ActorId);
        if (actor.IsFailure) return Result.Failure<NormalizedClose>(actor.Error);
        var requestHash = CanonicalRequestHash.Compute(
            operation,
            sessionId,
            command.ExpectedVersion,
            actor.Value,
            occurredAt,
            sourceSystem,
            sourceEventId,
            correlationId.Value,
            reason);
        return Result.Success(new NormalizedClose(
            operation, sessionId, command.ExpectedVersion, idempotencyKey, requestHash,
            actor.Value, occurredAt, sourceSystem, sourceEventId, correlationId.Value, reason));
    }

    private static Result<FeedSessionDto> Replay(
        FeedSessionWrite replay,
        string requestHash,
        string idempotencyKey) =>
        string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(ToDto(replay, true))
            : Result.Failure<FeedSessionDto>(Error.Conflict(
                "IVT.FeedSession.IdempotencyConflict",
                $"Idempotency key '{idempotencyKey}' is already used for a different feed-session command."));

    private static Result<FeedSessionDto> SourceConflict(
        string sourceSystem,
        string sourceEventId,
        FeedSessionWrite replay) =>
        Result.Failure<FeedSessionDto>(Error.Conflict(
            "IVT.FeedSession.SourceEventConflict",
            $"Source event '{sourceSystem}/{sourceEventId}' already changed feed session "
            + $"'{replay.Result.FeedSessionId}'. Reuse its original idempotency key."));

    private static Result<FeedSessionDto> VersionConflict(
        string feedSessionId,
        int expectedVersion,
        int? currentVersion) =>
        Result.Failure<FeedSessionDto>(Error.Conflict(
            "IVT.FeedSession.VersionConflict",
            currentVersion is null
                ? $"Feed session '{feedSessionId}' disappeared before version {expectedVersion} could be changed."
                : $"Feed session '{feedSessionId}' changed concurrently. Expected version {expectedVersion}; current version {currentVersion}."));

    private static FeedSessionDto ToDto(FeedSessionWrite write, bool replay)
    {
        var state = write.Result;
        return new FeedSessionDto(
            state.FeedSessionId,
            state.PlantId,
            state.EquipmentId,
            state.FeedPointId,
            state.MaterialLotId,
            state.MaterialId,
            state.ProcessLotId,
            state.WorkOrderId,
            state.ProcessId,
            state.RecipeId,
            state.RecipeVersion,
            state.MountedAt,
            state.MountedBy,
            state.UnmountedAt,
            state.UnmountedBy,
            state.Status,
            state.Version,
            write.Operation,
            write.ActorId,
            write.OccurredAt,
            write.SourceSystem,
            write.SourceEventId,
            write.CorrelationId,
            write.Reason,
            replay);
    }

    private static string? NormalizeOperation(string? operation) =>
        new[] { FeedSessionOperations.Mount, FeedSessionOperations.Unmount }
            .SingleOrDefault(candidate => string.Equals(
                candidate, operation?.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string? Required(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return normalized is not null && normalized.Length <= maxLength ? normalized : null;
    }

    private static OptionalText Optional(string? value, int maxLength)
    {
        var normalized = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new OptionalText(normalized, normalized?.Length > maxLength);
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private sealed record NormalizedMount(
        string FeedSessionId,
        string PlantId,
        string EquipmentId,
        string FeedPointId,
        string MaterialLotId,
        string? MaterialId,
        string? ProcessLotId,
        string? WorkOrderId,
        string? ProcessId,
        string? RecipeId,
        int? RecipeVersion,
        string IdempotencyKey,
        string RequestHash,
        string ActorId,
        DateTime OccurredAt,
        string SourceSystem,
        string SourceEventId,
        string? CorrelationId,
        string? Reason);

    private sealed record NormalizedClose(
        string Operation,
        string FeedSessionId,
        int ExpectedVersion,
        string IdempotencyKey,
        string RequestHash,
        string ActorId,
        DateTime OccurredAt,
        string SourceSystem,
        string SourceEventId,
        string? CorrelationId,
        string? Reason);

    private sealed record OptionalText(string? Value, bool Invalid);
}
