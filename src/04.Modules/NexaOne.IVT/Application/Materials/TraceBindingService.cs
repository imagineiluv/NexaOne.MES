using NexaOne.Common;
using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Application.Materials;

internal sealed class TraceBindingService
{
    private const decimal Decimal18IntegerLimit = 1_000_000_000_000m;
    private readonly ITraceBindingRepository _repository;
    private readonly IFdcTraceSource _traceSource;
    private readonly TraceMaintenanceGate _maintenance;

    public TraceBindingService(
        ITraceBindingRepository repository,
        IFdcTraceSource traceSource,
        TraceMaintenanceGate maintenance)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _traceSource = traceSource ?? throw new ArgumentNullException(nameof(traceSource));
        _maintenance = maintenance ?? throw new ArgumentNullException(nameof(maintenance));
    }

    public async Task<Result<TraceBindingDto>> ExecuteAsync(
        TraceBindingCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        var operation = command.Operation?.Trim();
        if (string.Equals(operation, TraceBindingOperations.Retire, StringComparison.OrdinalIgnoreCase))
            return await RetireAsync(command, ct);
        if (!string.Equals(operation, TraceBindingOperations.Create, StringComparison.OrdinalIgnoreCase))
        {
            return Result.Failure<TraceBindingDto>(Error.Validation(
                nameof(command.Operation), "Operation must be Create or Retire."));
        }

        var normalized = NormalizeCreate(command);
        if (normalized.IsFailure) return Result.Failure<TraceBindingDto>(normalized.Error);
        var request = normalized.Value;

        var replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        if (!_maintenance.IsOpen) return MaintenanceRequired();

        var sourceReplay = await _repository.GetBySourceEventAsync(
            request.SourceSystem, request.SourceEventId, ct);
        if (sourceReplay is not null)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.SourceEventConflict",
                $"Source event '{request.SourceSystem}/{request.SourceEventId}' already configured "
                + $"binding '{sourceReplay.Result.BindingId}'. Reuse its original idempotency key."));
        }

        try
        {
            _ = await _traceSource.ReadAsync(
                [new FdcTraceReadScope(
                    request.BindingId,
                    request.EquipmentId,
                    request.ParameterId,
                    request.EffectiveFrom,
                    null,
                    null,
                    null)],
                1,
                ct);
        }
        catch (FdcTraceGapException gap)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.SourceGap",
                $"Binding '{request.BindingId}' cannot start before the durable TRACE completeness "
                + $"boundary {gap.CompletenessBoundary:o}."));
        }

        var now = DateTime.UtcNow;
        var state = new TraceBindingState(
            request.BindingId,
            request.PlantId,
            request.EquipmentId,
            request.ParameterId,
            request.FeedPointId,
            request.CalculationMode,
            request.ScaleFactor,
            request.PulseQuantity,
            request.OutputUnit,
            request.EffectiveFrom,
            null,
            true,
            1,
            request.ActorId,
            now,
            request.ActorId,
            now);
        var write = new TraceBindingWrite(
            $"TBC_{Guid.NewGuid():N}",
            TraceBindingOperations.Create,
            request.IdempotencyKey,
            request.RequestHash,
            state,
            0,
            request.ActorId,
            request.OccurredAt,
            request.SourceSystem,
            request.SourceEventId,
            request.CorrelationId,
            request.Reason);

        if (await _repository.TryCreateAsync(state, write, ct)) return Result.Success(ToDto(write, false));

        replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        sourceReplay = await _repository.GetBySourceEventAsync(request.SourceSystem, request.SourceEventId, ct);
        if (sourceReplay is not null)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.SourceEventConflict",
                $"Source event '{request.SourceSystem}/{request.SourceEventId}' was committed concurrently."));
        }

        return Result.Failure<TraceBindingDto>(Error.Conflict(
            "IVT.TraceBinding.CreateConflict",
            $"Binding '{request.BindingId}' or its active TRACE source already exists."));
    }

    private async Task<Result<TraceBindingDto>> RetireAsync(
        TraceBindingCommand command,
        CancellationToken ct)
    {
        var normalized = NormalizeRetire(command);
        if (normalized.IsFailure) return Result.Failure<TraceBindingDto>(normalized.Error);
        var request = normalized.Value;

        var replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        if (!_maintenance.IsOpen) return MaintenanceRequired();
        var sourceReplay = await _repository.GetBySourceEventAsync(
            request.SourceSystem, request.SourceEventId, ct);
        if (sourceReplay is not null)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.SourceEventConflict",
                $"Source event '{request.SourceSystem}/{request.SourceEventId}' already changed "
                + $"binding '{sourceReplay.Result.BindingId}'."));
        }

        var current = await _repository.GetAsync(request.BindingId, ct);
        if (current is null)
            return Result.Failure<TraceBindingDto>(Error.NotFoundOf("TraceBinding", request.BindingId));
        if (current.Version != request.ExpectedVersion)
            return VersionConflict(request.BindingId, request.ExpectedVersion, current.Version);
        if (!current.IsActive)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.AlreadyRetired",
                $"Binding '{request.BindingId}' is already retired at version {current.Version}."));
        }
        if (request.EffectiveTo <= current.EffectiveFrom)
        {
            return Result.Failure<TraceBindingDto>(Error.Validation(
                nameof(command.EffectiveAt), "Retirement effective time must be after EffectiveFrom."));
        }

        var cursor = await _repository.GetIngestionCursorAsync(request.BindingId, ct);
        if (cursor is not null && cursor.LastCollectedAt >= request.EffectiveTo)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.RetireBeforeCursor",
                $"Binding '{request.BindingId}' already enqueued TRACE through "
                + $"{cursor.LastCollectedAt:o}; retirement must be later than that cursor."));
        }

        IReadOnlyList<FdcTraceSample> pending;
        try
        {
            pending = await _traceSource.ReadAsync(
                [new FdcTraceReadScope(
                    current.BindingId,
                    current.EquipmentId,
                    current.ParameterId,
                    current.EffectiveFrom,
                    request.EffectiveTo,
                    cursor?.LastCollectedAt,
                    cursor?.LastCollectId)],
                1,
                ct);
        }
        catch (FdcTraceGapException gap)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.SourceGap",
                $"Binding '{request.BindingId}' cannot retire because its durable TRACE cursor "
                + $"precedes completeness boundary {gap.CompletenessBoundary:o}."));
        }
        if (pending.Count > 0)
        {
            return Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.DrainRequired",
                $"Binding '{request.BindingId}' still has TRACE before {request.EffectiveTo:o} "
                + "that is not in the durable IVT ingestion inbox. Drain projection ingestion before retiring."));
        }

        var now = DateTime.UtcNow;
        var retired = current with
        {
            EffectiveTo = request.EffectiveTo,
            IsActive = false,
            Version = current.Version + 1,
            UpdatedBy = request.ActorId,
            UpdatedAt = now,
        };
        var write = new TraceBindingWrite(
            $"TBC_{Guid.NewGuid():N}",
            TraceBindingOperations.Retire,
            request.IdempotencyKey,
            request.RequestHash,
            retired,
            request.ExpectedVersion,
            request.ActorId,
            request.OccurredAt,
            request.SourceSystem,
            request.SourceEventId,
            request.CorrelationId,
            request.Reason);
        if (await _repository.TryRetireAsync(retired, request.ExpectedVersion, write, ct))
            return Result.Success(ToDto(write, false));

        replay = await _repository.GetByIdempotencyKeyAsync(request.IdempotencyKey, ct);
        if (replay is not null) return Replay(replay, request.RequestHash, request.IdempotencyKey);
        current = await _repository.GetAsync(request.BindingId, ct);
        return VersionConflict(request.BindingId, request.ExpectedVersion, current?.Version);
    }

    private static Result<NormalizedCreate> NormalizeCreate(TraceBindingCommand command)
    {
        var bindingId = Required(command.BindingId, 50);
        var plantId = Required(command.PlantId, 50);
        var equipmentId = Required(command.EquipmentId, 50);
        var parameterId = Required(command.ParameterId, 50);
        var feedPointId = Required(command.FeedPointId, 50);
        var outputUnit = Required(command.OutputUnit, 30);
        var key = Required(command.IdempotencyKey, 100);
        var sourceSystem = Required(command.SourceSystem, 50);
        var sourceEventId = Required(command.SourceEventId, 100);
        if (bindingId is null || plantId is null || equipmentId is null || parameterId is null
            || feedPointId is null || outputUnit is null || key is null
            || sourceSystem is null || sourceEventId is null)
        {
            return Result.Failure<NormalizedCreate>(Error.Validation(
                "IVT.TraceBinding.Required",
                "Binding, plant, equipment, parameter, feed point, unit, idempotency and source identity are required and must fit storage limits."));
        }
        if (command.ExpectedVersion != 0)
            return Result.Failure<NormalizedCreate>(Error.Validation(
                nameof(command.ExpectedVersion), "Create requires ExpectedVersion=0."));
        if (command.OccurredAt == default || command.EffectiveAt == default)
            return Result.Failure<NormalizedCreate>(Error.Validation(
                "IVT.TraceBinding.Timestamp", "OccurredAt and EffectiveAt are required."));
        if (command.ScaleFactor is null)
            return Result.Failure<NormalizedCreate>(Error.Validation(
                nameof(command.ScaleFactor), "ScaleFactor is required."));
        var scaleFactor = decimal.Round(
            command.ScaleFactor.Value, 6, MidpointRounding.AwayFromZero);
        if (!FitsPositiveDecimal18(command.ScaleFactor.Value, scaleFactor))
            return Result.Failure<NormalizedCreate>(Error.Validation(
                nameof(command.ScaleFactor),
                "ScaleFactor must be positive and fit DECIMAL(18,6)."));

        var mode = new[] { "Direct", "Pulse", "CounterDelta", "RateIntegrate" }
            .SingleOrDefault(candidate => string.Equals(candidate, command.CalculationMode?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (mode is null)
            return Result.Failure<NormalizedCreate>(Error.Validation(
                nameof(command.CalculationMode),
                "CalculationMode must be Direct, Pulse, CounterDelta, or RateIntegrate."));
        decimal? pulseQuantity = null;
        if (mode == "Pulse" && command.PulseQuantity is not null)
        {
            pulseQuantity = decimal.Round(
                command.PulseQuantity.Value, 6, MidpointRounding.AwayFromZero);
        }
        if (mode == "Pulse"
                ? command.PulseQuantity is null
                  || !FitsPositiveDecimal18(command.PulseQuantity.Value, pulseQuantity!.Value)
                : command.PulseQuantity is not null)
            return Result.Failure<NormalizedCreate>(Error.Validation(
                nameof(command.PulseQuantity),
                "Pulse requires a positive DECIMAL(18,6) PulseQuantity; other modes must omit it."));

        var actor = CommandActor.Resolve(command.ActorId);
        if (actor.IsFailure) return Result.Failure<NormalizedCreate>(actor.Error);
        var correlation = Optional(command.CorrelationId, 100);
        var reason = Optional(command.Reason, 500);
        if (correlation.Invalid || reason.Invalid)
            return Result.Failure<NormalizedCreate>(Error.Validation(
                "IVT.TraceBinding.OptionalLength", "CorrelationId or Reason exceeds its storage limit."));

        var occurredAt = Utc(command.OccurredAt);
        var effectiveFrom = Utc(command.EffectiveAt);
        var requestHash = CanonicalRequestHash.Compute(
            TraceBindingOperations.Create,
            bindingId,
            0,
            plantId,
            equipmentId,
            parameterId,
            feedPointId,
            mode,
            scaleFactor,
            pulseQuantity,
            outputUnit,
            effectiveFrom,
            actor.Value,
            occurredAt,
            sourceSystem,
            sourceEventId,
            correlation.Value,
            reason.Value);
        return Result.Success(new NormalizedCreate(
            bindingId,
            plantId,
            equipmentId,
            parameterId,
            feedPointId,
            mode,
            scaleFactor,
            pulseQuantity,
            outputUnit,
            effectiveFrom,
            key,
            requestHash,
            actor.Value,
            occurredAt,
            sourceSystem,
            sourceEventId,
            correlation.Value,
            reason.Value));
    }

    private static Result<NormalizedRetire> NormalizeRetire(TraceBindingCommand command)
    {
        var bindingId = Required(command.BindingId, 50);
        var key = Required(command.IdempotencyKey, 100);
        var sourceSystem = Required(command.SourceSystem, 50);
        var sourceEventId = Required(command.SourceEventId, 100);
        if (bindingId is null || key is null || sourceSystem is null || sourceEventId is null)
        {
            return Result.Failure<NormalizedRetire>(Error.Validation(
                "IVT.TraceBinding.Required",
                "Binding, idempotency and source identity are required and must fit storage limits."));
        }
        if (command.ExpectedVersion < 1)
            return Result.Failure<NormalizedRetire>(Error.Validation(
                nameof(command.ExpectedVersion), "Retire requires a positive ExpectedVersion."));
        if (command.OccurredAt == default || command.EffectiveAt == default)
            return Result.Failure<NormalizedRetire>(Error.Validation(
                "IVT.TraceBinding.Timestamp", "OccurredAt and EffectiveAt are required."));
        var actor = CommandActor.Resolve(command.ActorId);
        if (actor.IsFailure) return Result.Failure<NormalizedRetire>(actor.Error);
        var correlation = Optional(command.CorrelationId, 100);
        var reason = Optional(command.Reason, 500);
        if (correlation.Invalid || reason.Invalid)
            return Result.Failure<NormalizedRetire>(Error.Validation(
                "IVT.TraceBinding.OptionalLength", "CorrelationId or Reason exceeds its storage limit."));

        var occurredAt = Utc(command.OccurredAt);
        var effectiveTo = Utc(command.EffectiveAt);
        var now = DateTime.UtcNow;
        if (occurredAt > now || effectiveTo > now)
        {
            return Result.Failure<NormalizedRetire>(Error.Validation(
                "IVT.TraceBinding.FutureRetire",
                "Retire OccurredAt and EffectiveAt cannot be in the future because retirement deactivates the binding immediately."));
        }
        var requestHash = CanonicalRequestHash.Compute(
            TraceBindingOperations.Retire,
            bindingId,
            command.ExpectedVersion,
            effectiveTo,
            actor.Value,
            occurredAt,
            sourceSystem,
            sourceEventId,
            correlation.Value,
            reason.Value);
        return Result.Success(new NormalizedRetire(
            bindingId,
            command.ExpectedVersion,
            effectiveTo,
            key,
            requestHash,
            actor.Value,
            occurredAt,
            sourceSystem,
            sourceEventId,
            correlation.Value,
            reason.Value));
    }

    private static Result<TraceBindingDto> Replay(
        TraceBindingWrite replay,
        string requestHash,
        string idempotencyKey) =>
        string.Equals(replay.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(ToDto(replay, true))
            : Result.Failure<TraceBindingDto>(Error.Conflict(
                "IVT.TraceBinding.IdempotencyConflict",
                $"Idempotency key '{idempotencyKey}' is already used for a different binding command."));

    private static TraceBindingDto ToDto(TraceBindingWrite write, bool replay)
    {
        var state = write.Result;
        return new TraceBindingDto(
            state.BindingId,
            state.PlantId,
            state.EquipmentId,
            state.ParameterId,
            state.FeedPointId,
            state.CalculationMode,
            state.ScaleFactor,
            state.PulseQuantity,
            state.OutputUnit,
            state.EffectiveFrom,
            state.EffectiveTo,
            state.IsActive,
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

    private static Result<TraceBindingDto> VersionConflict(
        string bindingId,
        int expectedVersion,
        int? currentVersion) =>
        Result.Failure<TraceBindingDto>(Error.Conflict(
            "IVT.TraceBinding.VersionConflict",
            currentVersion is null
                ? $"Binding '{bindingId}' disappeared before version {expectedVersion} could be changed."
                : $"Binding '{bindingId}' changed concurrently. Expected version {expectedVersion}; current version {currentVersion}."));

    private Result<TraceBindingDto> MaintenanceRequired() =>
        Result.Failure<TraceBindingDto>(Error.Conflict(
            "IVT.TraceBinding.MaintenanceRequired",
            $"TRACE binding changes require quiesced maintenance: {_maintenance.Reason}"));

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

    private static bool FitsPositiveDecimal18(decimal original, decimal rounded) =>
        original > 0m && rounded > 0m && rounded < Decimal18IntegerLimit;

    private sealed record NormalizedCreate(
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
        string IdempotencyKey,
        string RequestHash,
        string ActorId,
        DateTime OccurredAt,
        string SourceSystem,
        string SourceEventId,
        string? CorrelationId,
        string? Reason);

    private sealed record NormalizedRetire(
        string BindingId,
        int ExpectedVersion,
        DateTime EffectiveTo,
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
