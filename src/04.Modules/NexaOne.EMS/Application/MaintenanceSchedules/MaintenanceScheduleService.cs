using System.Globalization;
using NexaOne.Application.Idempotency;
using NexaOne.Common;
using NexaOne.ServiceContracts.Ems;

namespace NexaOne.EMS.Application.MaintenanceSchedules;

/// <summary>
/// PM 반복 정의의 검증과 수동 도래 확인을 소유한다. 자동 W/O 생성은 별도 실행 경로가
/// 준비되기 전까지 명시적으로 닫혀 있으며, 조회는 파일 쿼리 레지스트리를 사용한다.
/// </summary>
public sealed class MaintenanceScheduleService
{
    private static readonly string[] TriggerTypes = ["Calendar", "Meter", "Condition"];
    private static readonly string[] IntervalUnits = ["Hour", "Day", "Week", "Month", "Year"];
    private static readonly string[] ClientChannels = ["MES", "MOBILE", "POP"];
    private readonly IMaintenanceScheduleRepository _repository;

    public MaintenanceScheduleService(IMaintenanceScheduleRepository repository) => _repository = repository;

    public async Task<Result<MaintenanceScheduleRecord>> CreateAsync(
        MaintenanceScheduleCreateCommand command,
        CancellationToken ct = default)
    {
        var actor = RequiredText(command.ActorId);
        if (actor is null) return InvalidSchedule(nameof(command.ActorId), "An authenticated actor is required.");
        if (actor.Length > 50) return InvalidSchedule(nameof(command.ActorId), "ActorId cannot exceed 50 characters.");

        var definition = BuildDefinition(
            command.ScheduleId, command.MaintenancePlanId, command.TriggerType,
            command.IntervalValue, command.IntervalUnit, command.TimeZoneId, command.NextDueAt,
            command.MeterParameterId, command.MeterThreshold, command.MeterBaselineValue,
            command.NextMeterDueValue, command.ConditionRuleId, command.AutoCreateWorkOrder,
            command.IsActive);
        if (definition.IsFailure) return Result.Failure<MaintenanceScheduleRecord>(definition.Error);
        if (!await _repository.MaintenancePlanExistsAsync(definition.Value.MaintenancePlanId, ct))
            return Result.Failure<MaintenanceScheduleRecord>(
                Error.NotFoundOf("MaintenancePlan", definition.Value.MaintenancePlanId));

        var now = DateTime.UtcNow;
        var schedule = definition.Value.ToRecord(actor, now);
        if (!await _repository.TryCreateAsync(schedule, ct))
            return Result.Failure<MaintenanceScheduleRecord>(Error.Conflict(
                "EMS.MaintenanceSchedule.AlreadyExists",
                "The schedule id or maintenance plan already has a schedule."));
        return Result.Success(schedule);
    }

    public async Task<Result<MaintenanceScheduleRecord>> UpdateAsync(
        MaintenanceScheduleUpdateCommand command,
        CancellationToken ct = default)
    {
        if (command.ExpectedVersion < 1)
            return InvalidSchedule(nameof(command.ExpectedVersion), "ExpectedVersion must be positive.");
        var actor = RequiredText(command.ActorId);
        if (actor is null) return InvalidSchedule(nameof(command.ActorId), "An authenticated actor is required.");
        if (actor.Length > 50) return InvalidSchedule(nameof(command.ActorId), "ActorId cannot exceed 50 characters.");

        var definition = BuildDefinition(
            command.ScheduleId, command.MaintenancePlanId, command.TriggerType,
            command.IntervalValue, command.IntervalUnit, command.TimeZoneId, command.NextDueAt,
            command.MeterParameterId, command.MeterThreshold, command.MeterBaselineValue,
            command.NextMeterDueValue, command.ConditionRuleId, command.AutoCreateWorkOrder,
            command.IsActive);
        if (definition.IsFailure) return Result.Failure<MaintenanceScheduleRecord>(definition.Error);

        var existing = await _repository.GetAsync(definition.Value.ScheduleId, ct);
        if (existing is null)
            return Result.Failure<MaintenanceScheduleRecord>(
                Error.NotFoundOf("MaintenanceSchedule", definition.Value.ScheduleId));
        if (existing.Version != command.ExpectedVersion)
            return VersionConflict<MaintenanceScheduleRecord>(command.ExpectedVersion, existing.Version);
        if (!await _repository.MaintenancePlanExistsAsync(definition.Value.MaintenancePlanId, ct))
            return Result.Failure<MaintenanceScheduleRecord>(
                Error.NotFoundOf("MaintenancePlan", definition.Value.MaintenancePlanId));

        var now = DateTime.UtcNow;
        var updated = definition.Value.ToRecord(actor, now) with
        {
            LastDueAt = existing.LastDueAt,
            Version = command.ExpectedVersion + 1,
            CreatedBy = existing.CreatedBy,
            CreatedAt = existing.CreatedAt,
        };
        if (!await _repository.TryUpdateAsync(updated, command.ExpectedVersion, ct))
        {
            var winner = await _repository.GetAsync(updated.ScheduleId, ct);
            return VersionConflict<MaintenanceScheduleRecord>(command.ExpectedVersion, winner?.Version);
        }
        return Result.Success(updated);
    }

    public async Task<Result<MaintenanceScheduleAcknowledgementRecord>> AcknowledgeAsync(
        MaintenanceScheduleAcknowledgeCommand command,
        CancellationToken ct = default)
    {
        var validation = ValidateAcknowledgement(command);
        if (validation is not null)
            return Result.Failure<MaintenanceScheduleAcknowledgementRecord>(validation);

        var scheduleId = command.ScheduleId.Trim();
        var idempotencyKey = command.IdempotencyKey.Trim();
        var actor = command.ActorId!.Trim();
        var acknowledgedAt = Utc(command.AcknowledgedAt);
        var channel = Canonical(ClientChannels, command.ClientChannel)!;
        var deviceId = Text(command.DeviceId);
        var correlationId = Text(command.CorrelationId);
        var remark = Text(command.Remark);
        var requestHash = Hash(
            scheduleId, command.ExpectedVersion, acknowledgedAt, command.ObservedMeterValue,
            command.ConditionMet, actor, channel, deviceId, correlationId, remark);

        var replay = await _repository.GetAcknowledgementAsync(idempotencyKey, ct);
        if (replay is not null) return Replay(replay, requestHash);

        var schedule = await _repository.GetAsync(scheduleId, ct);
        if (schedule is null)
            return Result.Failure<MaintenanceScheduleAcknowledgementRecord>(
                Error.NotFoundOf("MaintenanceSchedule", scheduleId));
        if (schedule.Version != command.ExpectedVersion)
            return VersionConflict<MaintenanceScheduleAcknowledgementRecord>(command.ExpectedVersion, schedule.Version);
        if (!schedule.IsActive)
            return Result.Failure<MaintenanceScheduleAcknowledgementRecord>(Error.Conflict(
                "EMS.MaintenanceSchedule.Inactive", "An inactive schedule cannot be acknowledged."));
        if (schedule.AutoCreateWorkOrder)
            return Result.Failure<MaintenanceScheduleAcknowledgementRecord>(Error.Conflict(
                "EMS.MaintenanceSchedule.AutoWorkOrderUnavailable",
                "AUTO_CREATE_WO is unavailable until an automatic work-order execution path is configured."));

        var occurrence = BuildOccurrence(schedule, command, acknowledgedAt);
        if (occurrence.IsFailure)
            return Result.Failure<MaintenanceScheduleAcknowledgementRecord>(occurrence.Error);

        var now = DateTime.UtcNow;
        var nextVersion = schedule.Version + 1;
        var updated = schedule with
        {
            LastDueAt = occurrence.Value.LastDueAt,
            NextDueAt = occurrence.Value.NextDueAt,
            MeterBaselineValue = occurrence.Value.MeterBaselineValue,
            NextMeterDueValue = occurrence.Value.NextMeterDueValue,
            Version = nextVersion,
            UpdatedBy = actor,
            UpdatedAt = now,
        };
        var acknowledgement = new MaintenanceScheduleAcknowledgementRecord(
            $"MSA_{Guid.NewGuid():N}", schedule.ScheduleId, schedule.MaintenancePlanId,
            schedule.TriggerType, occurrence.Value.DueAt, occurrence.Value.NextDueAt,
            occurrence.Value.MeterDueValue, occurrence.Value.ObservedMeterValue,
            occurrence.Value.NextMeterDueValue, occurrence.Value.ConditionRuleId,
            occurrence.Value.ConditionMet, acknowledgedAt, actor, remark, idempotencyKey,
            requestHash, channel, deviceId, correlationId, schedule.Version, nextVersion, now);

        if (await _repository.TryAcknowledgeAsync(schedule: updated, command.ExpectedVersion, acknowledgement, ct))
            return Result.Success(acknowledgement);

        replay = await _repository.GetAcknowledgementAsync(idempotencyKey, ct);
        if (replay is not null) return Replay(replay, requestHash);
        return Result.Failure<MaintenanceScheduleAcknowledgementRecord>(Error.Conflict(
            "EMS.MaintenanceSchedule.ConcurrentWrite",
            "The schedule changed concurrently; reload it and retry with the current version."));
    }

    private static Result<ScheduleDefinition> BuildDefinition(
        string scheduleId,
        string maintenancePlanId,
        string triggerType,
        decimal? intervalValue,
        string? intervalUnit,
        string timeZoneId,
        DateTime? nextDueAt,
        string? meterParameterId,
        decimal? meterThreshold,
        decimal? meterBaselineValue,
        decimal? nextMeterDueValue,
        string? conditionRuleId,
        bool autoCreateWorkOrder,
        bool isActive)
    {
        var id = RequiredText(scheduleId);
        if (id is null) return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(scheduleId), "ScheduleId is required."));
        if (id.Length > 50) return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(scheduleId), "ScheduleId cannot exceed 50 characters."));
        var planId = RequiredText(maintenancePlanId);
        if (planId is null) return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(maintenancePlanId), "MaintenancePlanId is required."));
        if (planId.Length > 50) return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(maintenancePlanId), "MaintenancePlanId cannot exceed 50 characters."));
        var trigger = Canonical(TriggerTypes, triggerType);
        if (trigger is null) return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(triggerType), "TriggerType must be Calendar, Meter, or Condition."));
        var zoneId = RequiredText(timeZoneId);
        if (zoneId is null || zoneId.Length > 100 || !TryFindTimeZone(zoneId, out _))
            return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(timeZoneId), "TimeZoneId must identify a supported time zone."));
        if (autoCreateWorkOrder)
            return Result.Failure<ScheduleDefinition>(Error.Conflict(
                "EMS.MaintenanceSchedule.AutoWorkOrderUnavailable",
                "AUTO_CREATE_WO cannot be enabled until an automatic work-order execution path is configured."));

        var unit = Text(intervalUnit);
        var meterId = Text(meterParameterId);
        var conditionId = Text(conditionRuleId);
        DateTime? due = nextDueAt is null ? null : Utc(nextDueAt.Value);

        if (trigger == "Calendar")
        {
            unit = Canonical(IntervalUnits, unit);
            if (intervalValue is null or <= 0m)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(intervalValue), "Calendar interval must be positive."));
            if (unit is null)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(intervalUnit), "Calendar IntervalUnit must be Hour, Day, Week, Month, or Year."));
            if (unit is "Month" or "Year" && decimal.Truncate(intervalValue.Value) != intervalValue.Value)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(intervalValue), "Month and Year intervals must be whole numbers."));
            if (due is null)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(nextDueAt), "Calendar NextDueAt is required."));
            if (meterId is not null || meterThreshold is not null || meterBaselineValue is not null
                || nextMeterDueValue is not null || conditionId is not null)
                return Result.Failure<ScheduleDefinition>(Error.Validation("TriggerFields", "Calendar schedules cannot contain meter or condition fields."));
        }
        else if (trigger == "Meter")
        {
            if (meterId is null)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(meterParameterId), "MeterParameterId is required."));
            if (meterThreshold is null or <= 0m)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(meterThreshold), "MeterThreshold must be positive."));
            if (meterBaselineValue is null or < 0m)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(meterBaselineValue), "MeterBaselineValue must be non-negative."));
            if (nextMeterDueValue is null || nextMeterDueValue <= meterBaselineValue)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(nextMeterDueValue), "NextMeterDueValue must be greater than the baseline."));
            if (intervalValue is not null || unit is not null || due is not null || conditionId is not null)
                return Result.Failure<ScheduleDefinition>(Error.Validation("TriggerFields", "Meter schedules cannot contain calendar or condition fields."));
        }
        else
        {
            if (conditionId is null)
                return Result.Failure<ScheduleDefinition>(Error.Validation(nameof(conditionRuleId), "ConditionRuleId is required."));
            if (intervalValue is not null || unit is not null || due is not null || meterId is not null
                || meterThreshold is not null || meterBaselineValue is not null || nextMeterDueValue is not null)
                return Result.Failure<ScheduleDefinition>(Error.Validation("TriggerFields", "Condition schedules cannot contain calendar or meter fields."));
        }

        return Result.Success(new ScheduleDefinition(
            id, planId, trigger, intervalValue, unit, zoneId, due, meterId,
            meterThreshold, meterBaselineValue, nextMeterDueValue, conditionId,
            autoCreateWorkOrder, isActive));
    }

    private static Error? ValidateAcknowledgement(MaintenanceScheduleAcknowledgeCommand command)
    {
        if (string.IsNullOrWhiteSpace(command.ScheduleId)) return Error.Validation(nameof(command.ScheduleId), "ScheduleId is required.");
        if (command.ScheduleId.Trim().Length > 50) return Error.Validation(nameof(command.ScheduleId), "ScheduleId cannot exceed 50 characters.");
        if (command.ExpectedVersion < 1) return Error.Validation(nameof(command.ExpectedVersion), "ExpectedVersion must be positive.");
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey)) return Error.Validation(nameof(command.IdempotencyKey), "IdempotencyKey is required.");
        if (command.IdempotencyKey.Trim().Length > 150) return Error.Validation(nameof(command.IdempotencyKey), "IdempotencyKey cannot exceed 150 characters.");
        if (command.AcknowledgedAt == default) return Error.Validation(nameof(command.AcknowledgedAt), "AcknowledgedAt is required.");
        if (string.IsNullOrWhiteSpace(command.ActorId)) return Error.Validation(nameof(command.ActorId), "An authenticated actor is required.");
        if (command.ActorId.Trim().Length > 50) return Error.Validation(nameof(command.ActorId), "ActorId cannot exceed 50 characters.");
        if (Canonical(ClientChannels, command.ClientChannel) is null)
            return Error.Validation(nameof(command.ClientChannel), "ClientChannel must be MES, MOBILE, or POP.");
        if (Text(command.DeviceId)?.Length > 100) return Error.Validation(nameof(command.DeviceId), "DeviceId cannot exceed 100 characters.");
        if (Text(command.CorrelationId)?.Length > 100) return Error.Validation(nameof(command.CorrelationId), "CorrelationId cannot exceed 100 characters.");
        if (Text(command.Remark)?.Length > 500) return Error.Validation(nameof(command.Remark), "Remark cannot exceed 500 characters.");
        return null;
    }

    private static Result<DueOccurrence> BuildOccurrence(
        MaintenanceScheduleRecord schedule,
        MaintenanceScheduleAcknowledgeCommand command,
        DateTime acknowledgedAt)
    {
        if (schedule.TriggerType == "Calendar")
        {
            if (command.ObservedMeterValue is not null || command.ConditionMet is not null)
                return InvalidOccurrence(
                    "EMS.MaintenanceSchedule.InvalidAcknowledgement",
                    "Calendar acknowledgement cannot contain meter or condition evidence.");
            if (schedule.NextDueAt is null || schedule.IntervalValue is null || schedule.IntervalUnit is null)
                return InvalidOccurrence("EMS.MaintenanceSchedule.InvalidState", "The calendar schedule has incomplete due state.");
            var due = Utc(schedule.NextDueAt.Value);
            if (acknowledgedAt < due)
                return InvalidOccurrence("EMS.MaintenanceSchedule.NotDue", "The calendar schedule is not due yet.");
            var next = AdvanceCalendar(due, acknowledgedAt, schedule.IntervalValue.Value, schedule.IntervalUnit, schedule.TimeZoneId);
            if (next.IsFailure) return Result.Failure<DueOccurrence>(next.Error);
            return Result.Success(new DueOccurrence(
                due, next.Value, null, null, null, null, null, due, null));
        }

        if (schedule.TriggerType == "Meter")
        {
            if (command.ConditionMet is not null)
                return InvalidOccurrence(
                    "EMS.MaintenanceSchedule.InvalidAcknowledgement",
                    "Meter acknowledgement cannot contain condition evidence.");
            if (schedule.NextMeterDueValue is null || schedule.MeterThreshold is null)
                return InvalidOccurrence("EMS.MaintenanceSchedule.InvalidState", "The meter schedule has incomplete due state.");
            if (command.ObservedMeterValue is null || command.ObservedMeterValue < schedule.NextMeterDueValue)
                return InvalidOccurrence("EMS.MaintenanceSchedule.NotDue", "ObservedMeterValue has not reached the meter due value.");
            var observed = command.ObservedMeterValue.Value;
            var nextMeter = observed + schedule.MeterThreshold.Value;
            return Result.Success(new DueOccurrence(
                null, null, schedule.NextMeterDueValue, observed, nextMeter, null, null,
                acknowledgedAt, observed));
        }

        if (schedule.TriggerType == "Condition")
        {
            if (command.ObservedMeterValue is not null)
                return InvalidOccurrence(
                    "EMS.MaintenanceSchedule.InvalidAcknowledgement",
                    "Condition acknowledgement cannot contain meter evidence.");
            if (string.IsNullOrWhiteSpace(schedule.ConditionRuleId))
                return InvalidOccurrence("EMS.MaintenanceSchedule.InvalidState", "The condition schedule has no rule identity.");
            if (command.ConditionMet is not true)
                return InvalidOccurrence("EMS.MaintenanceSchedule.NotDue", "ConditionMet must be true to acknowledge a condition schedule.");
            return Result.Success(new DueOccurrence(
                null, null, null, null, null, schedule.ConditionRuleId, true,
                acknowledgedAt, null));
        }

        return InvalidOccurrence("EMS.MaintenanceSchedule.InvalidState", "The schedule trigger type is unknown.");
    }

    private static Result<DateTime> AdvanceCalendar(
        DateTime dueUtc,
        DateTime acknowledgedAtUtc,
        decimal interval,
        string unit,
        string timeZoneId)
    {
        if (!TryFindTimeZone(timeZoneId, out var zone))
            return Result.Failure<DateTime>(Error.Conflict(
                "EMS.MaintenanceSchedule.InvalidTimeZone", "The configured time zone is no longer available."));

        try
        {
            var local = TimeZoneInfo.ConvertTimeFromUtc(Utc(dueUtc), zone!);
            for (var i = 0; i < 100_000; i++)
            {
                local = unit switch
                {
                    "Hour" => local.AddHours(decimal.ToDouble(interval)),
                    "Day" => local.AddDays(decimal.ToDouble(interval)),
                    "Week" => local.AddDays(decimal.ToDouble(interval * 7m)),
                    "Month" => local.AddMonths(decimal.ToInt32(interval)),
                    "Year" => local.AddYears(decimal.ToInt32(interval)),
                    _ => local,
                };
                local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                while (zone!.IsInvalidTime(local)) local = local.AddMinutes(1);
                var candidate = TimeZoneInfo.ConvertTimeToUtc(local, zone);
                if (candidate > acknowledgedAtUtc) return Result.Success(candidate);
            }
        }
        catch (ArgumentOutOfRangeException) { /* invalid persisted interval/state */ }
        catch (OverflowException) { /* invalid persisted interval/state */ }

        return Result.Failure<DateTime>(Error.Conflict(
            "EMS.MaintenanceSchedule.AdvanceLimit",
            "The next calendar occurrence could not be calculated within the safety limit."));
    }

    private static bool TryFindTimeZone(string id, out TimeZoneInfo? zone)
    {
        try { zone = TimeZoneInfo.FindSystemTimeZoneById(id); return true; }
        catch (TimeZoneNotFoundException) { zone = null; return false; }
        catch (InvalidTimeZoneException) { zone = null; return false; }
    }

    private static Result<MaintenanceScheduleAcknowledgementRecord> Replay(
        MaintenanceScheduleAcknowledgementRecord stored,
        string requestHash)
        => string.Equals(stored.RequestHash, requestHash, StringComparison.Ordinal)
            ? Result.Success(stored)
            : Result.Failure<MaintenanceScheduleAcknowledgementRecord>(Error.Conflict(
                "EMS.MaintenanceSchedule.IdempotencyConflict",
                "The idempotency key was already used for different acknowledgement data."));

    private static Result<T> VersionConflict<T>(int expected, int? actual)
        => Result.Failure<T>(Error.Conflict(
            "EMS.MaintenanceSchedule.VersionConflict",
            $"Expected schedule version {expected}, but the current version is {(actual?.ToString(CultureInfo.InvariantCulture) ?? "missing")}."));

    private static Result<MaintenanceScheduleRecord> InvalidSchedule(string code, string description)
        => Result.Failure<MaintenanceScheduleRecord>(Error.Validation(code, description));
    private static Result<DueOccurrence> InvalidOccurrence(string code, string description)
        => Result.Failure<DueOccurrence>(Error.Conflict(code, description));
    private static string? RequiredText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Canonical(IEnumerable<string> values, string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : values.FirstOrDefault(candidate => candidate.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };
    private static string Hash(params object?[] values)
        => CanonicalRequestHash.Compute(values);

    private sealed record ScheduleDefinition(
        string ScheduleId,
        string MaintenancePlanId,
        string TriggerType,
        decimal? IntervalValue,
        string? IntervalUnit,
        string TimeZoneId,
        DateTime? NextDueAt,
        string? MeterParameterId,
        decimal? MeterThreshold,
        decimal? MeterBaselineValue,
        decimal? NextMeterDueValue,
        string? ConditionRuleId,
        bool AutoCreateWorkOrder,
        bool IsActive)
    {
        public MaintenanceScheduleRecord ToRecord(string actor, DateTime now) => new(
            ScheduleId, MaintenancePlanId, TriggerType, IntervalValue, IntervalUnit, TimeZoneId,
            null, NextDueAt, MeterParameterId, MeterThreshold, MeterBaselineValue,
            NextMeterDueValue, ConditionRuleId, AutoCreateWorkOrder, IsActive, 1,
            actor, now, actor, now);
    }

    private sealed record DueOccurrence(
        DateTime? DueAt,
        DateTime? NextDueAt,
        decimal? MeterDueValue,
        decimal? ObservedMeterValue,
        decimal? NextMeterDueValue,
        string? ConditionRuleId,
        bool? ConditionMet,
        DateTime LastDueAt,
        decimal? MeterBaselineValue);
}
