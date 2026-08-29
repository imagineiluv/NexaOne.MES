using NexaOne.Application.Auditing;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Application.Est;

public sealed class UtilityService
{
    private static readonly HashSet<string> ReadingModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cumulative", "Delta",
    };

    private static readonly Dictionary<string, string> MeterEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Replacement"] = "Replacement",
        ["Reset"] = "Reset",
        ["Rollover"] = "Rollover",
        ["Calibration"] = "Calibration",
    };

    private readonly IUtilityRepository _repository;

    public UtilityService(IUtilityRepository repository) => _repository = repository;

    public async Task<Result<UtilityMeterRecord>> SaveMeterAsync(
        UtilityMeterCommand command,
        CancellationToken ct = default)
    {
        var error = ValidateMeter(command);
        if (error is not null) return Result.Failure<UtilityMeterRecord>(error);
        var actor = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actor.IsFailure) return Result.Failure<UtilityMeterRecord>(actor.Error);

        var meterId = command.MeterId.Trim();
        var idempotencyKey = command.IdempotencyKey.Trim();
        var meter = new UtilityMeterRecord(
            meterId, command.MeterName.Trim(), command.PlantId.Trim(), Text(command.EquipmentId),
            command.UtilityType.Trim(), command.Unit.Trim(), Text(command.FdcParameterId),
            CanonicalMode(command.ReadingMode), command.ScaleFactor, command.CostPerUnit,
            command.CarbonPerUnit, command.IsActive, command.ExpectedVersion + 1);
        var requestHash = HashMeterSave(meter, command.ExpectedVersion, actor.Value);

        var previousCommand = await _repository.GetMeterSaveCommandAsync(idempotencyKey, ct);
        if (previousCommand is not null)
            return await ReplayMeterSaveAsync(previousCommand, requestHash, ct);

        var existing = await _repository.GetMeterAsync(meterId, ct);
        var currentVersion = existing?.ConfigVersion ?? 0;
        if (currentVersion != command.ExpectedVersion)
            return Result.Failure<UtilityMeterRecord>(Error.Conflict(
                "EST.Utility.StaleMeterVersion",
                $"Utility meter '{meterId}' is version {currentVersion}, not {command.ExpectedVersion}."));
        if (existing is not null && SameConfiguration(existing, meter))
            return Result.Failure<UtilityMeterRecord>(Error.Conflict(
                "EST.Utility.NoConfigurationChange",
                $"Utility meter '{meterId}' already has the requested configuration."));

        if (await _repository.TrySaveMeterAsync(
                meter, command.ExpectedVersion, idempotencyKey, requestHash, actor.Value, ct))
            return Result.Success(meter);

        previousCommand = await _repository.GetMeterSaveCommandAsync(idempotencyKey, ct);
        return previousCommand is not null
            ? await ReplayMeterSaveAsync(previousCommand, requestHash, ct)
            : Result.Failure<UtilityMeterRecord>(Error.Conflict(
                "EST.Utility.ConcurrentMeterChange",
                $"Utility meter '{meter.MeterId}' changed concurrently; reload and retry."));
    }

    public async Task<Result<UtilityReadingRecord>> RecordReadingAsync(
        UtilityReadingCommand command,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.MeterId))
            return InvalidReading(nameof(command.MeterId), "MeterId is required.");
        if (string.IsNullOrWhiteSpace(command.Source))
            return InvalidReading(nameof(command.Source), "Source is required.");
        if (string.IsNullOrWhiteSpace(command.SourceEventId))
            return InvalidReading(nameof(command.SourceEventId), "SourceEventId is required.");
        if (command.RawValue < 0m)
            return InvalidReading(nameof(command.RawValue), "RawValue cannot be negative.");
        var actor = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actor.IsFailure) return Result.Failure<UtilityReadingRecord>(actor.Error);

        var recordedAt = Utc(command.RecordedAt);
        var existing = await _repository.GetReadingAsync(command.Source.Trim(), command.SourceEventId.Trim(), ct);
        if (existing is not null)
        {
            var originalConfig = new UtilityMeterRecord(
                existing.MeterId, string.Empty, existing.PlantId, existing.EquipmentId,
                string.Empty, existing.Unit, null, existing.ReadingMode, 1m,
                existing.CostPerUnit, existing.CarbonPerUnit, true, existing.MeterConfigVersion);
            return Replay(existing, HashReading(command, originalConfig, recordedAt, existing.NormalizedValue));
        }

        var meter = await _repository.GetMeterAsync(command.MeterId.Trim(), ct);
        if (meter is null)
            return Result.Failure<UtilityReadingRecord>(Error.NotFoundOf("UtilityMeter", command.MeterId));
        if (!meter.IsActive)
            return Result.Failure<UtilityReadingRecord>(Error.Conflict(
                "EST.Utility.MeterInactive", $"Utility meter '{meter.MeterId}' is inactive."));

        var normalized = decimal.Round(command.RawValue * meter.ScaleFactor, 6, MidpointRounding.AwayFromZero);
        var requestHash = HashReading(command, meter, recordedAt, normalized);

        var reading = new UtilityReadingRecord(
            $"URD_{Guid.NewGuid():N}", meter.MeterId,
            Text(command.EquipmentId) ?? meter.EquipmentId, Text(command.ProcessLotId), Text(command.WorkOrderId),
            Text(command.RecipeId), command.RecipeVersion, command.RawValue, normalized, meter.Unit,
            command.Source.Trim(), command.SourceEventId.Trim(), requestHash,
            string.IsNullOrWhiteSpace(command.Quality) ? "Unknown" : command.Quality.Trim(),
            recordedAt, actor.Value, DateTime.UtcNow, meter.ConfigVersion,
            meter.PlantId, meter.ReadingMode, meter.CostPerUnit, meter.CarbonPerUnit);

        if (await _repository.TryAddReadingAsync(reading, ct)) return Result.Success(reading);

        existing = await _repository.GetReadingAsync(reading.Source, reading.SourceEventId, ct);
        return existing is null
            ? Result.Failure<UtilityReadingRecord>(Error.Conflict(
                "EST.Utility.ConcurrentWrite", "The utility reading could not be persisted because of a concurrent write."))
            : Replay(existing, requestHash);
    }

    public async Task<Result<UtilityMeterEventRecord>> RecordMeterEventAsync(
        UtilityMeterEventCommand command,
        CancellationToken ct = default)
    {
        var validation = ValidateMeterEvent(command);
        if (validation is not null) return Result.Failure<UtilityMeterEventRecord>(validation);
        var actor = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actor.IsFailure) return Result.Failure<UtilityMeterEventRecord>(actor.Error);

        var meterId = command.MeterId.Trim();
        var idempotencyKey = command.IdempotencyKey.Trim();
        var eventType = CanonicalEventType(command.EventType);
        var occurredAt = Utc(command.OccurredAt);
        var reason = command.Reason.Trim();
        var requestHash = HashMeterEvent(
            idempotencyKey, meterId, eventType, occurredAt, reason,
            command.PreviousValue, command.AfterValue, command.BaselineValue, actor.Value);

        var existing = await _repository.GetMeterEventAsync(idempotencyKey, ct);
        if (existing is not null) return Replay(existing, requestHash);

        var meter = await _repository.GetMeterAsync(meterId, ct);
        var meterError = ValidateEventMeter(meter, meterId);
        if (meterError is not null) return Result.Failure<UtilityMeterEventRecord>(meterError);

        var meterEvent = new UtilityMeterEventRecord(
            $"UEV_{Guid.NewGuid():N}", idempotencyKey, requestHash, meter!.MeterId,
            meter.PlantId, meter.EquipmentId, eventType, occurredAt, reason,
            command.PreviousValue, command.AfterValue, command.BaselineValue,
            meter.Unit, actor.Value, DateTime.UtcNow, meter.ConfigVersion);
        if (await _repository.TryAddMeterEventAsync(meterEvent, ct)) return Result.Success(meterEvent);

        existing = await _repository.GetMeterEventAsync(idempotencyKey, ct);
        if (existing is not null) return Replay(existing, requestHash);

        // Repository INSERT ... SELECT rechecks active/mode/assignment/unit atomically. A zero-row write
        // without an idempotency winner means the master changed after the service read.
        return Result.Failure<UtilityMeterEventRecord>(Error.Conflict(
            "EST.Utility.ConcurrentMeterChange",
            "The meter changed while its discontinuity event was being recorded; reload and retry."));
    }

    public async Task<Result<IReadOnlyList<UtilityMeterEventRecord>>> GetMeterEventHistoryAsync(
        string meterId,
        DateTime from,
        DateTime to,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(meterId))
            return Result.Failure<IReadOnlyList<UtilityMeterEventRecord>>(
                Error.Validation(nameof(meterId), "MeterId is required."));
        var rangeStart = Utc(from);
        var rangeEnd = Utc(to);
        if (rangeEnd <= rangeStart)
            return Result.Failure<IReadOnlyList<UtilityMeterEventRecord>>(
                Error.Validation("The history end must be after its start."));
        var meter = await _repository.GetMeterAsync(meterId.Trim(), ct);
        if (meter is null)
            return Result.Failure<IReadOnlyList<UtilityMeterEventRecord>>(
                Error.NotFoundOf("UtilityMeter", meterId));

        return Result.Success<IReadOnlyList<UtilityMeterEventRecord>>(
            await _repository.GetMeterEventsAsync(meter.MeterId, rangeStart, rangeEnd, ct));
    }

    public async Task<Result<IReadOnlyList<UtilityMeterConfigHistoryRecord>>> GetMeterConfigHistoryAsync(
        string meterId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(meterId))
            return Result.Failure<IReadOnlyList<UtilityMeterConfigHistoryRecord>>(
                Error.Validation(nameof(meterId), "MeterId is required."));
        var normalized = meterId.Trim();
        if (await _repository.GetMeterAsync(normalized, ct) is null)
            return Result.Failure<IReadOnlyList<UtilityMeterConfigHistoryRecord>>(
                Error.NotFoundOf("UtilityMeter", normalized));
        return Result.Success<IReadOnlyList<UtilityMeterConfigHistoryRecord>>(
            await _repository.GetMeterConfigHistoryAsync(normalized, ct));
    }

    public async Task<Result<UtilitySummaryRecord>> SummarizeAsync(
        UtilitySummaryCommand command,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.MeterId))
            return Result.Failure<UtilitySummaryRecord>(Error.Validation(nameof(command.MeterId), "MeterId is required."));
        if (string.IsNullOrWhiteSpace(command.PeriodType))
            return Result.Failure<UtilitySummaryRecord>(Error.Validation(nameof(command.PeriodType), "PeriodType is required."));
        var actor = CommandActor.Resolve(command.ActorId, nameof(command.ActorId));
        if (actor.IsFailure) return Result.Failure<UtilitySummaryRecord>(actor.Error);
        var start = Utc(command.PeriodStart);
        var end = Utc(command.PeriodEnd);
        if (end <= start)
            return Result.Failure<UtilitySummaryRecord>(Error.Validation("PeriodEnd must be after PeriodStart."));

        var meter = await _repository.GetMeterAsync(command.MeterId.Trim(), ct);
        if (meter is null)
            return Result.Failure<UtilitySummaryRecord>(Error.NotFoundOf("UtilityMeter", command.MeterId));

        var periodReadings = (await _repository.GetPeriodReadingsAsync(meter.MeterId, start, end, false, ct))
            .Where(r => r.Quality.Equals("Good", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.RecordedAt)
            .ThenBy(r => r.CreatedAt)
            .ThenBy(r => r.ReadingId, StringComparer.Ordinal)
            .ToList();
        if (periodReadings.Count == 0)
            return Result.Failure<UtilitySummaryRecord>(Error.NotFound(
                "EST.Utility.NoReadings", "No good utility readings exist in the requested period."));

        var cumulative = periodReadings[0].ReadingMode.Equals(
            "Cumulative", StringComparison.OrdinalIgnoreCase);
        var readings = cumulative
            ? (await _repository.GetPeriodReadingsAsync(meter.MeterId, start, end, true, ct))
                .Where(r => r.Quality.Equals("Good", StringComparison.OrdinalIgnoreCase))
                .OrderBy(r => r.RecordedAt)
                .ThenBy(r => r.CreatedAt)
                .ThenBy(r => r.ReadingId, StringComparer.Ordinal)
                .ToList()
            : periodReadings;

        var config = readings[0];
        if (readings.Any(reading => reading.MeterConfigVersion != config.MeterConfigVersion
            || !reading.PlantId.Equals(config.PlantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(reading.EquipmentId, config.EquipmentId, StringComparison.OrdinalIgnoreCase)
            || !reading.Unit.Equals(config.Unit, StringComparison.OrdinalIgnoreCase)
            || !reading.ReadingMode.Equals(config.ReadingMode, StringComparison.OrdinalIgnoreCase)
            || reading.CostPerUnit != config.CostPerUnit
            || reading.CarbonPerUnit != config.CarbonPerUnit))
            return Result.Failure<UtilitySummaryRecord>(Error.Conflict(
                "EST.Utility.ConfigurationBoundary",
                "The period crosses a meter configuration boundary. Split the summary at the configuration change."));

        decimal consumption;
        decimal? startReading = null;
        decimal? endReading = null;
        if (cumulative)
        {
            if (readings.Count < 2)
                return Result.Failure<UtilitySummaryRecord>(Error.Validation(
                    "A cumulative meter needs a baseline and an end reading."));
            startReading = readings[0].NormalizedValue;
            endReading = readings[^1].NormalizedValue;
            var meterEvents = (await _repository.GetMeterEventsAsync(
                    meter.MeterId, readings[0].RecordedAt, end, ct))
                .Where(e => e.OccurredAt > readings[0].RecordedAt
                            && e.OccurredAt <= readings[^1].RecordedAt)
                .OrderBy(e => e.OccurredAt)
                .ThenBy(e => e.CreatedAt)
                .ThenBy(e => e.EventId, StringComparer.Ordinal)
                .ToList();
            if (meterEvents.Any(e => e.MeterConfigVersion != config.MeterConfigVersion))
                return Result.Failure<UtilitySummaryRecord>(Error.Conflict(
                    "EST.Utility.ConfigurationBoundary",
                    "The period crosses a meter configuration boundary. Split the summary at the configuration change."));
            var calculation = CalculateCumulativeConsumption(readings, meterEvents, config.Unit);
            if (calculation.IsFailure)
                return Result.Failure<UtilitySummaryRecord>(calculation.Error);
            consumption = calculation.Value;
        }
        else
        {
            consumption = readings.Sum(r => r.NormalizedValue);
        }

        var summary = new UtilitySummaryRecord(
            SummaryId(meter.MeterId, command.PeriodType, start, end), meter.MeterId, config.PlantId,
            config.EquipmentId, command.PeriodType.Trim(), start, end, startReading, endReading,
            consumption, config.Unit, config.CostPerUnit * consumption, config.CarbonPerUnit * consumption,
            DateTime.UtcNow);
        await _repository.SaveSummaryAsync(summary, actor.Value, ct);
        return Result.Success(summary);
    }

    private static Error? ValidateMeter(UtilityMeterCommand c)
    {
        if (string.IsNullOrWhiteSpace(c.MeterId)) return Error.Validation(nameof(c.MeterId), "MeterId is required.");
        if (string.IsNullOrWhiteSpace(c.MeterName)) return Error.Validation(nameof(c.MeterName), "MeterName is required.");
        if (string.IsNullOrWhiteSpace(c.PlantId)) return Error.Validation(nameof(c.PlantId), "PlantId is required.");
        if (string.IsNullOrWhiteSpace(c.UtilityType)) return Error.Validation(nameof(c.UtilityType), "UtilityType is required.");
        if (string.IsNullOrWhiteSpace(c.Unit)) return Error.Validation(nameof(c.Unit), "Unit is required.");
        if (!ReadingModes.Contains(c.ReadingMode))
            return Error.Validation(nameof(c.ReadingMode), "ReadingMode must be Cumulative or Delta.");
        if (c.ScaleFactor <= 0m) return Error.Validation(nameof(c.ScaleFactor), "ScaleFactor must be greater than zero.");
        if (c.CostPerUnit is < 0m || c.CarbonPerUnit is < 0m)
            return Error.Validation("CostPerUnit and CarbonPerUnit cannot be negative.");
        if (c.ExpectedVersion < 0)
            return Error.Validation(nameof(c.ExpectedVersion), "ExpectedVersion cannot be negative.");
        if (string.IsNullOrWhiteSpace(c.IdempotencyKey))
            return Error.Validation(nameof(c.IdempotencyKey), "IdempotencyKey is required.");
        if (c.IdempotencyKey.Trim().Length > 100)
            return Error.Validation(nameof(c.IdempotencyKey), "IdempotencyKey cannot exceed 100 characters.");
        return null;
    }

    private async Task<Result<UtilityMeterRecord>> ReplayMeterSaveAsync(
        UtilityMeterSaveCommandRecord command,
        string requestHash,
        CancellationToken ct)
    {
        if (!string.Equals(command.RequestHash, requestHash, StringComparison.Ordinal))
            return Result.Failure<UtilityMeterRecord>(Error.Conflict(
                "EST.Utility.MeterSaveIdempotencyConflict",
                "The idempotency key already represents a different meter configuration command."));

        var snapshot = (await _repository.GetMeterConfigHistoryAsync(command.MeterId, ct))
            .SingleOrDefault(history => history.ConfigVersion == command.ResultVersion);
        if (snapshot is null)
            return Result.Failure<UtilityMeterRecord>(Error.Conflict(
                "EST.Utility.MeterSaveEvidenceMissing",
                "The saved meter command has no matching immutable configuration snapshot."));
        return Result.Success(new UtilityMeterRecord(
            snapshot.MeterId, snapshot.MeterName, snapshot.PlantId, snapshot.EquipmentId,
            snapshot.UtilityType, snapshot.Unit, snapshot.FdcParameterId, snapshot.ReadingMode,
            snapshot.ScaleFactor, snapshot.CostPerUnit, snapshot.CarbonPerUnit,
            snapshot.IsActive, snapshot.ConfigVersion));
    }

    private static bool SameConfiguration(UtilityMeterRecord current, UtilityMeterRecord requested)
        => current.MeterName == requested.MeterName
           && current.PlantId == requested.PlantId
           && current.EquipmentId == requested.EquipmentId
           && current.UtilityType == requested.UtilityType
           && current.Unit == requested.Unit
           && current.FdcParameterId == requested.FdcParameterId
           && current.ReadingMode == requested.ReadingMode
           && current.ScaleFactor == requested.ScaleFactor
           && current.CostPerUnit == requested.CostPerUnit
           && current.CarbonPerUnit == requested.CarbonPerUnit
           && current.IsActive == requested.IsActive;

    private static string HashMeterSave(UtilityMeterRecord meter, int expectedVersion, string actorId)
        => CanonicalRequestHash.Compute(
            meter.MeterId, meter.MeterName, meter.PlantId, meter.EquipmentId,
            meter.UtilityType, meter.Unit, meter.FdcParameterId, meter.ReadingMode,
            meter.ScaleFactor, meter.CostPerUnit, meter.CarbonPerUnit, meter.IsActive,
            expectedVersion, actorId);

    private static Error? ValidateMeterEvent(UtilityMeterEventCommand c)
    {
        if (string.IsNullOrWhiteSpace(c.IdempotencyKey))
            return Error.Validation(nameof(c.IdempotencyKey), "IdempotencyKey is required.");
        if (c.IdempotencyKey.Trim().Length > 100)
            return Error.Validation(nameof(c.IdempotencyKey), "IdempotencyKey cannot exceed 100 characters.");
        if (string.IsNullOrWhiteSpace(c.MeterId))
            return Error.Validation(nameof(c.MeterId), "MeterId is required.");
        if (string.IsNullOrWhiteSpace(c.EventType) || !MeterEventTypes.ContainsKey(c.EventType.Trim()))
            return Error.Validation(nameof(c.EventType),
                "EventType must be Replacement, Reset, Rollover, or Calibration.");
        if (c.OccurredAt == default)
            return Error.Validation(nameof(c.OccurredAt), "OccurredAt is required.");
        if (string.IsNullOrWhiteSpace(c.Reason))
            return Error.Validation(nameof(c.Reason), "Reason is required.");
        if (c.Reason.Trim().Length > 500)
            return Error.Validation(nameof(c.Reason), "Reason cannot exceed 500 characters.");

        var hasBoundaryPair = c.PreviousValue.HasValue && c.AfterValue.HasValue && !c.BaselineValue.HasValue;
        var hasBaseline = !c.PreviousValue.HasValue && !c.AfterValue.HasValue && c.BaselineValue.HasValue;
        if (!hasBoundaryPair && !hasBaseline)
            return Error.Validation("Provide either PreviousValue and AfterValue together, or BaselineValue alone.");
        if (c.PreviousValue is < 0m || c.AfterValue is < 0m || c.BaselineValue is < 0m)
            return Error.Validation("Meter event values cannot be negative.");
        if (hasBoundaryPair && c.PreviousValue == c.AfterValue)
            return Error.Validation("PreviousValue and AfterValue must describe an actual discontinuity.");
        return null;
    }

    private static Error? ValidateEventMeter(UtilityMeterRecord? meter, string meterId)
    {
        if (meter is null) return Error.NotFoundOf("UtilityMeter", meterId);
        if (!meter.IsActive)
            return Error.Conflict("EST.Utility.MeterInactive", $"Utility meter '{meter.MeterId}' is inactive.");
        if (!meter.ReadingMode.Equals("Cumulative", StringComparison.OrdinalIgnoreCase))
            return Error.Conflict(
                "EST.Utility.EventRequiresCumulativeMeter",
                "Meter discontinuity events are valid only for cumulative meters.");
        return null;
    }

    private static Result<decimal> CalculateCumulativeConsumption(
        IReadOnlyList<UtilityReadingRecord> readings,
        IReadOnlyList<UtilityMeterEventRecord> meterEvents,
        string unit)
    {
        var consumption = 0m;
        for (var readingIndex = 1; readingIndex < readings.Count; readingIndex++)
        {
            var previousReading = readings[readingIndex - 1];
            var currentReading = readings[readingIndex];
            var cursor = previousReading.NormalizedValue;
            var intervalEvents = meterEvents
                .Where(e => e.OccurredAt > previousReading.RecordedAt
                            && e.OccurredAt <= currentReading.RecordedAt)
                .ToList();

            foreach (var meterEvent in intervalEvents)
            {
                if (!meterEvent.Unit.Equals(unit, StringComparison.OrdinalIgnoreCase))
                    return Result.Failure<decimal>(Error.Conflict(
                        "EST.Utility.UnitChanged",
                        "Meter events and readings from different units cannot be summarized together."));

                if (meterEvent.BaselineValue.HasValue)
                {
                    // An explicit baseline starts a new known continuity segment. The unknown interval
                    // before it is deliberately excluded rather than guessed as normal consumption.
                    cursor = meterEvent.BaselineValue.Value;
                    continue;
                }

                var beforeEvent = meterEvent.PreviousValue!.Value - cursor;
                if (beforeEvent < 0m)
                    return InvalidContinuity(meterEvent.EventId);
                consumption += beforeEvent;
                cursor = meterEvent.AfterValue!.Value;
            }

            var afterLastEvent = currentReading.NormalizedValue - cursor;
            if (afterLastEvent < 0m)
            {
                if (intervalEvents.Count == 0)
                    return Result.Failure<decimal>(Error.Conflict(
                        "EST.Utility.MeterReset",
                        "The cumulative meter decreased without a recorded reset, rollover, replacement, or calibration event."));
                return InvalidContinuity(intervalEvents[^1].EventId);
            }
            consumption += afterLastEvent;
        }
        return Result.Success(consumption);
    }

    private static Result<decimal> InvalidContinuity(string eventId)
        => Result.Failure<decimal>(Error.Conflict(
            "EST.Utility.DiscontinuityMismatch",
            $"Meter event '{eventId}' does not form a non-decreasing continuity segment with its surrounding readings."));

    private static Result<UtilityReadingRecord> InvalidReading(string code, string description)
        => Result.Failure<UtilityReadingRecord>(Error.Validation(code, description));

    private static Result<UtilityReadingRecord> Replay(UtilityReadingRecord existing, string requestHash)
        => string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(existing)
            : Result.Failure<UtilityReadingRecord>(Error.Conflict(
                "EST.Utility.IdempotencyConflict", "The source event already represents a different reading."));

    private static Result<UtilityMeterEventRecord> Replay(
        UtilityMeterEventRecord existing, string requestHash)
        => string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(existing)
            : Result.Failure<UtilityMeterEventRecord>(Error.Conflict(
                "EST.Utility.EventIdempotencyConflict",
                "The idempotency key already represents a different meter event."));

    private static string HashReading(UtilityReadingCommand c, UtilityMeterRecord meter, DateTime at, decimal normalized)
        => CanonicalRequestHash.Compute(
            meter.MeterId, meter.ConfigVersion, meter.PlantId,
            Text(c.EquipmentId) ?? meter.EquipmentId,
            meter.ReadingMode, meter.Unit, meter.CostPerUnit, meter.CarbonPerUnit,
            c.RawValue, normalized, c.Source.Trim(),
            c.SourceEventId.Trim(), at, Text(c.EquipmentId), Text(c.ProcessLotId),
            Text(c.WorkOrderId), Text(c.RecipeId), c.RecipeVersion,
            string.IsNullOrWhiteSpace(c.Quality) ? "Unknown" : c.Quality.Trim());

    private static string HashMeterEvent(
        string idempotencyKey,
        string meterId,
        string eventType,
        DateTime occurredAt,
        string reason,
        decimal? previousValue,
        decimal? afterValue,
        decimal? baselineValue,
        string actorUserId)
        => CanonicalRequestHash.Compute(
            idempotencyKey, meterId, eventType, occurredAt, reason,
            previousValue, afterValue, baselineValue, actorUserId);

    private static string SummaryId(string meterId, string periodType, DateTime start, DateTime end)
        => CanonicalRequestHash.CreateId("USM_", 32, meterId, periodType.Trim(), start, end);

    private static string CanonicalMode(string mode)
        => mode.Equals("Cumulative", StringComparison.OrdinalIgnoreCase) ? "Cumulative" : "Delta";

    private static string CanonicalEventType(string eventType)
        => MeterEventTypes[eventType.Trim()];

    private static string? Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
