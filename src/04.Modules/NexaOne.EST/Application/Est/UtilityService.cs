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

        var meter = new UtilityMeterRecord(
            command.MeterId.Trim(), command.MeterName.Trim(), command.PlantId.Trim(), Text(command.EquipmentId),
            command.UtilityType.Trim(), command.Unit.Trim(), Text(command.FdcParameterId),
            CanonicalMode(command.ReadingMode), command.ScaleFactor, command.CostPerUnit,
            command.CarbonPerUnit, command.IsActive);
        await _repository.SaveMeterAsync(meter, actor.Value, ct);
        return Result.Success(meter);
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

        var meter = await _repository.GetMeterAsync(command.MeterId.Trim(), ct);
        if (meter is null)
            return Result.Failure<UtilityReadingRecord>(Error.NotFoundOf("UtilityMeter", command.MeterId));
        if (!meter.IsActive)
            return Result.Failure<UtilityReadingRecord>(Error.Conflict(
                "EST.Utility.MeterInactive", $"Utility meter '{meter.MeterId}' is inactive."));

        var recordedAt = Utc(command.RecordedAt);
        var normalized = decimal.Round(command.RawValue * meter.ScaleFactor, 6, MidpointRounding.AwayFromZero);
        var requestHash = HashReading(command, meter, recordedAt, normalized);
        var existing = await _repository.GetReadingAsync(command.Source.Trim(), command.SourceEventId.Trim(), ct);
        if (existing is not null) return Replay(existing, requestHash);

        var reading = new UtilityReadingRecord(
            $"URD_{Guid.NewGuid():N}", meter.MeterId,
            Text(command.EquipmentId) ?? meter.EquipmentId, Text(command.ProcessLotId), Text(command.WorkOrderId),
            Text(command.RecipeId), command.RecipeVersion, command.RawValue, normalized, meter.Unit,
            command.Source.Trim(), command.SourceEventId.Trim(), requestHash,
            string.IsNullOrWhiteSpace(command.Quality) ? "Unknown" : command.Quality.Trim(),
            recordedAt, actor.Value, DateTime.UtcNow);

        if (await _repository.TryAddReadingAsync(reading, ct)) return Result.Success(reading);

        existing = await _repository.GetReadingAsync(reading.Source, reading.SourceEventId, ct);
        return existing is null
            ? Result.Failure<UtilityReadingRecord>(Error.Conflict(
                "EST.Utility.ConcurrentWrite", "The utility reading could not be persisted because of a concurrent write."))
            : Replay(existing, requestHash);
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

        var cumulative = meter.ReadingMode.Equals("Cumulative", StringComparison.OrdinalIgnoreCase);
        var readings = (await _repository.GetPeriodReadingsAsync(meter.MeterId, start, end, cumulative, ct))
            .Where(r => r.Quality.Equals("Good", StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.RecordedAt)
            .ToList();
        if (readings.Count == 0)
            return Result.Failure<UtilitySummaryRecord>(Error.NotFound(
                "EST.Utility.NoReadings", "No good utility readings exist in the requested period."));

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
            consumption = endReading.Value - startReading.Value;
            if (consumption < 0m)
                return Result.Failure<UtilitySummaryRecord>(Error.Conflict(
                    "EST.Utility.MeterReset", "The cumulative meter decreased; record a reset/baseline before summarizing."));
        }
        else
        {
            consumption = readings.Sum(r => r.NormalizedValue);
        }

        var summary = new UtilitySummaryRecord(
            SummaryId(meter.MeterId, command.PeriodType, start, end), meter.MeterId, meter.PlantId,
            meter.EquipmentId, command.PeriodType.Trim(), start, end, startReading, endReading,
            consumption, meter.Unit, meter.CostPerUnit * consumption, meter.CarbonPerUnit * consumption,
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
        return null;
    }

    private static Result<UtilityReadingRecord> InvalidReading(string code, string description)
        => Result.Failure<UtilityReadingRecord>(Error.Validation(code, description));

    private static Result<UtilityReadingRecord> Replay(UtilityReadingRecord existing, string requestHash)
        => string.Equals(existing.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(existing)
            : Result.Failure<UtilityReadingRecord>(Error.Conflict(
                "EST.Utility.IdempotencyConflict", "The source event already represents a different reading."));

    private static string HashReading(UtilityReadingCommand c, UtilityMeterRecord meter, DateTime at, decimal normalized)
        => CanonicalRequestHash.Compute(
            meter.MeterId, c.RawValue, normalized, meter.Unit, c.Source.Trim(),
            c.SourceEventId.Trim(), at, Text(c.EquipmentId), Text(c.ProcessLotId),
            Text(c.WorkOrderId), Text(c.RecipeId), c.RecipeVersion,
            string.IsNullOrWhiteSpace(c.Quality) ? "Unknown" : c.Quality.Trim());

    private static string SummaryId(string meterId, string periodType, DateTime start, DateTime end)
        => CanonicalRequestHash.CreateId("USM_", 32, meterId, periodType.Trim(), start, end);

    private static string CanonicalMode(string mode)
        => mode.Equals("Cumulative", StringComparison.OrdinalIgnoreCase) ? "Cumulative" : "Delta";

    private static string? Text(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
}
