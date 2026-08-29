using FluentAssertions;
using NexaOne.EST.Application.Est;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.UnitTests.Services;

public sealed class UtilityServiceTests
{
    [Fact]
    public async Task Cumulative_meter_scales_readings_and_calculates_cost_and_carbon()
    {
        var repo = new MemoryRepository();
        var service = new UtilityService(repo);
        (await service.SaveMeterAsync(new UtilityMeterCommand(
            "POWER-01", "Main power", "PLANT01", "Electricity", "kWh", "Cumulative",
            ScaleFactor: 0.1m, EquipmentId: "EQ01", CostPerUnit: 150m, CarbonPerUnit: 0.45m,
            ActorId: "operator", IdempotencyKey: "meter:create:power-01"))).IsSuccess.Should().BeTrue();

        var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        await service.RecordReadingAsync(Reading(1000m, "1", start));
        await service.RecordReadingAsync(Reading(1125m, "2", start.AddHours(1)));

        var result = await service.SummarizeAsync(new UtilitySummaryCommand(
            "POWER-01", "Hourly", start, start.AddHours(2), "operator"));

        result.IsSuccess.Should().BeTrue();
        result.Value.StartReading.Should().Be(100m);
        result.Value.EndReading.Should().Be(112.5m);
        result.Value.Consumption.Should().Be(12.5m);
        result.Value.CostAmount.Should().Be(1875m);
        result.Value.CarbonAmount.Should().Be(5.625m);
        repo.Summaries.Should().ContainSingle();
    }

    [Fact]
    public async Task Replays_same_source_event_but_rejects_changed_value()
    {
        var repo = new MemoryRepository { Meter = Meter() };
        var service = new UtilityService(repo);
        var command = Reading(10m, "evt-1", DateTime.UtcNow);

        var first = await service.RecordReadingAsync(command);
        var replay = await service.RecordReadingAsync(command);
        var conflict = await service.RecordReadingAsync(command with { RawValue = 11m });

        first.IsSuccess.Should().BeTrue();
        replay.Value.ReadingId.Should().Be(first.Value.ReadingId);
        conflict.IsFailure.Should().BeTrue();
        repo.Readings.Should().ContainSingle();
    }

    [Theory]
    [InlineData("Replacement")]
    [InlineData("Reset")]
    [InlineData("Rollover")]
    [InlineData("Calibration")]
    public async Task Boundary_event_splits_cumulative_consumption_without_counting_the_counter_jump(
        string eventType)
    {
        var repo = new MemoryRepository { Meter = Meter() };
        var service = new UtilityService(repo);
        var start = new DateTime(2026, 8, 26, 0, 0, 0, DateTimeKind.Utc);
        (await service.RecordReadingAsync(Reading(100m, "before", start))).IsSuccess.Should().BeTrue();
        (await service.RecordReadingAsync(Reading(30m, "after", start.AddHours(2)))).IsSuccess.Should().BeTrue();

        var meterEvent = await service.RecordMeterEventAsync(new UtilityMeterEventCommand(
            $"event:{eventType}", "POWER-01", eventType, start.AddHours(1),
            "meter continuity changed", PreviousValue: 150m, AfterValue: 0m,
            ActorId: "maintenance-user"));
        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            "POWER-01", "Shift", start, start.AddHours(3), "operator"));

        meterEvent.IsSuccess.Should().BeTrue();
        summary.IsSuccess.Should().BeTrue();
        summary.Value.Consumption.Should().Be(80m, "50 before and 30 after the discontinuity are real use");
        repo.MeterEvents.Should().ContainSingle()
            .Which.ActorUserId.Should().Be("maintenance-user");
    }

    [Fact]
    public async Task Positive_calibration_jump_is_excluded_from_consumption()
    {
        var repo = new MemoryRepository { Meter = Meter() };
        var service = new UtilityService(repo);
        var start = new DateTime(2026, 8, 26, 3, 0, 0, DateTimeKind.Utc);
        await service.RecordReadingAsync(Reading(100m, "cal-before", start));
        await service.RecordReadingAsync(Reading(130m, "cal-after", start.AddHours(2)));
        await service.RecordMeterEventAsync(new UtilityMeterEventCommand(
            "event:calibration", "POWER-01", "Calibration", start.AddHours(1),
            "calibration offset correction", PreviousValue: 110m, AfterValue: 120m,
            ActorId: "calibrator"));

        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            "POWER-01", "Shift", start, start.AddHours(3), "operator"));

        summary.IsSuccess.Should().BeTrue();
        summary.Value.Consumption.Should().Be(20m, "the +10 calibration offset is not utility use");
    }

    [Fact]
    public async Task Baseline_event_starts_a_new_known_segment_instead_of_guessing_the_unknown_jump()
    {
        var repo = new MemoryRepository { Meter = Meter() };
        var service = new UtilityService(repo);
        var start = new DateTime(2026, 8, 26, 6, 0, 0, DateTimeKind.Utc);
        await service.RecordReadingAsync(Reading(100m, "baseline-before", start));
        await service.RecordReadingAsync(Reading(25m, "baseline-after", start.AddHours(2)));
        await service.RecordMeterEventAsync(new UtilityMeterEventCommand(
            "event:baseline", "POWER-01", "Replacement", start.AddHours(1),
            "old meter final value unavailable", BaselineValue: 10m, ActorId: "maintenance-user"));

        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            "POWER-01", "Shift", start, start.AddHours(3), "operator"));

        summary.IsSuccess.Should().BeTrue();
        summary.Value.Consumption.Should().Be(15m);
    }

    [Fact]
    public async Task Meter_event_is_idempotent_and_preserves_the_first_actor_and_reason()
    {
        var repo = new MemoryRepository { Meter = Meter() };
        var service = new UtilityService(repo);
        var command = new UtilityMeterEventCommand(
            "event:idem", "POWER-01", "Reset", DateTime.UtcNow,
            "operator reset", PreviousValue: 10m, AfterValue: 0m, ActorId: "operator-a");

        var first = await service.RecordMeterEventAsync(command);
        var replay = await service.RecordMeterEventAsync(command);
        var conflict = await service.RecordMeterEventAsync(command with { Reason = "different reason" });

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.EventId.Should().Be(first.Value.EventId);
        replay.Value.ActorUserId.Should().Be("operator-a");
        replay.Value.Reason.Should().Be("operator reset");
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EST.Utility.EventIdempotencyConflict");
        repo.MeterEvents.Should().ContainSingle();
    }

    [Fact]
    public async Task Delta_meter_and_mismatched_boundary_event_fail_closed()
    {
        var deltaRepo = new MemoryRepository { Meter = Meter() with { ReadingMode = "Delta" } };
        var delta = await new UtilityService(deltaRepo).RecordMeterEventAsync(new UtilityMeterEventCommand(
            "event:delta", "POWER-01", "Reset", DateTime.UtcNow,
            "invalid delta reset", PreviousValue: 10m, AfterValue: 0m, ActorId: "operator"));

        var repo = new MemoryRepository { Meter = Meter() };
        var service = new UtilityService(repo);
        var start = new DateTime(2026, 8, 26, 9, 0, 0, DateTimeKind.Utc);
        await service.RecordReadingAsync(Reading(100m, "mismatch-before", start));
        await service.RecordReadingAsync(Reading(20m, "mismatch-after", start.AddHours(2)));
        await service.RecordMeterEventAsync(new UtilityMeterEventCommand(
            "event:mismatch", "POWER-01", "Reset", start.AddHours(1),
            "wrong old boundary", PreviousValue: 90m, AfterValue: 0m, ActorId: "operator"));
        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            "POWER-01", "Shift", start, start.AddHours(3), "operator"));

        delta.IsFailure.Should().BeTrue();
        delta.Error.Code.Should().Be("EST.Utility.EventRequiresCumulativeMeter");
        summary.IsFailure.Should().BeTrue();
        summary.Error.Code.Should().Be("EST.Utility.DiscontinuityMismatch");
    }

    [Fact]
    public async Task Meter_write_fails_closed_without_an_actor()
    {
        var previous = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        try
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
            var result = await new UtilityService(new MemoryRepository()).SaveMeterAsync(
                new UtilityMeterCommand("POWER-01", "Main", "P1", "Electricity", "kWh", "Delta",
                    IdempotencyKey: "meter:no-actor"));

            result.IsFailure.Should().BeTrue();
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previous;
        }
    }

    [Fact]
    public async Task Meter_configuration_is_versioned_with_actor_history_and_cas()
    {
        var repo = new MemoryRepository();
        var service = new UtilityService(repo);
        var first = await service.SaveMeterAsync(new UtilityMeterCommand(
            "POWER-01", "Main", "P1", "Electricity", "kWh", "Cumulative",
            CostPerUnit: 100m, ActorId: "engineer-1", IdempotencyKey: "meter:create"));
        var second = await service.SaveMeterAsync(new UtilityMeterCommand(
            "POWER-01", "Main", "P1", "Electricity", "kWh", "Cumulative",
            CostPerUnit: 120m, ActorId: "engineer-2", ExpectedVersion: 1,
            IdempotencyKey: "meter:update:2"));

        first.Value.ConfigVersion.Should().Be(1);
        second.Value.ConfigVersion.Should().Be(2);
        repo.MeterConfigHistory.Select(x => (x.ConfigVersion, x.ChangedBy, x.CostPerUnit))
            .Should().Equal((1, "engineer-1", 100m), (2, "engineer-2", 120m));

        repo.RejectNextMeterSave = true;
        var conflict = await service.SaveMeterAsync(new UtilityMeterCommand(
            "POWER-01", "Main", "P1", "Electricity", "kWh", "Cumulative",
            CostPerUnit: 130m, ActorId: "engineer-3", ExpectedVersion: 2,
            IdempotencyKey: "meter:update:3"));
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EST.Utility.ConcurrentMeterChange");
    }

    [Fact]
    public async Task Meter_configuration_command_replays_immutable_result_and_rejects_key_reuse()
    {
        var repo = new MemoryRepository();
        var service = new UtilityService(repo);
        var command = new UtilityMeterCommand(
            "POWER-01", "Main", "P1", "Electricity", "kWh", "Cumulative",
            CostPerUnit: 100m, ActorId: "engineer-1", IdempotencyKey: "meter:create:replay");

        var first = await service.SaveMeterAsync(command);
        var replay = await service.SaveMeterAsync(command);
        var conflict = await service.SaveMeterAsync(command with { CostPerUnit = 120m });

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().Be(first.Value);
        repo.MeterConfigHistory.Should().ContainSingle();
        repo.MeterSaveCommands.Should().ContainSingle();
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EST.Utility.MeterSaveIdempotencyConflict");
    }

    [Fact]
    public async Task Summary_rejects_configuration_boundary_but_original_reading_replays_after_change()
    {
        var repo = new MemoryRepository();
        var service = new UtilityService(repo);
        await service.SaveMeterAsync(new UtilityMeterCommand(
            "POWER-01", "Main", "P1", "Electricity", "kWh", "Cumulative",
            CostPerUnit: 100m, ActorId: "engineer", IdempotencyKey: "meter:boundary:create"));
        var start = new DateTime(2026, 8, 27, 0, 0, 0, DateTimeKind.Utc);
        var originalCommand = Reading(100m, "cfg-1", start);
        var original = await service.RecordReadingAsync(originalCommand);

        await service.SaveMeterAsync(new UtilityMeterCommand(
            "POWER-01", "Main", "P1", "Electricity", "kWh", "Cumulative",
            CostPerUnit: 120m, ActorId: "engineer", ExpectedVersion: 1,
            IdempotencyKey: "meter:boundary:update"));
        var replay = await service.RecordReadingAsync(originalCommand);
        await service.RecordReadingAsync(Reading(130m, "cfg-2", start.AddHours(1)));
        var summary = await service.SummarizeAsync(new UtilitySummaryCommand(
            "POWER-01", "Shift", start, start.AddHours(2), "operator"));

        replay.IsSuccess.Should().BeTrue();
        replay.Value.ReadingId.Should().Be(original.Value.ReadingId);
        summary.IsFailure.Should().BeTrue();
        summary.Error.Code.Should().Be("EST.Utility.ConfigurationBoundary");
    }

    private static UtilityReadingCommand Reading(decimal raw, string eventId, DateTime at) => new(
        "POWER-01", raw, "FDC", eventId, at, ActorId: "operator");

    private static UtilityMeterRecord Meter() => new(
        "POWER-01", "Main power", "PLANT01", "EQ01", "Electricity", "kWh", null,
        "Cumulative", 1m, 150m, 0.45m, true);

    private sealed class MemoryRepository : IUtilityRepository
    {
        public UtilityMeterRecord? Meter { get; set; }
        public List<UtilityReadingRecord> Readings { get; } = new();
        public List<UtilityMeterEventRecord> MeterEvents { get; } = new();
        public List<UtilityMeterConfigHistoryRecord> MeterConfigHistory { get; } = new();
        public List<UtilityMeterSaveCommandRecord> MeterSaveCommands { get; } = new();
        public List<UtilitySummaryRecord> Summaries { get; } = new();
        public bool RejectNextMeterSave { get; set; }

        public Task<UtilityMeterRecord?> GetMeterAsync(string meterId, CancellationToken ct = default)
            => Task.FromResult(Meter?.MeterId == meterId ? Meter : null);

        public Task<UtilityMeterSaveCommandRecord?> GetMeterSaveCommandAsync(
            string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(MeterSaveCommands.SingleOrDefault(
                command => command.IdempotencyKey == idempotencyKey));

        public Task<bool> TrySaveMeterAsync(
            UtilityMeterRecord meter,
            int expectedVersion,
            string idempotencyKey,
            string requestHash,
            string actorId,
            CancellationToken ct = default)
        {
            if (RejectNextMeterSave)
            {
                RejectNextMeterSave = false;
                return Task.FromResult(false);
            }
            if (MeterSaveCommands.Any(command => command.IdempotencyKey == idempotencyKey)
                || (Meter?.ConfigVersion ?? 0) != expectedVersion
                || meter.ConfigVersion != expectedVersion + 1)
                return Task.FromResult(false);
            var savedAt = DateTime.UtcNow;
            Meter = meter;
            MeterConfigHistory.Add(new UtilityMeterConfigHistoryRecord(
                $"H{meter.ConfigVersion}", meter.MeterId, meter.ConfigVersion,
                meter.MeterName, meter.PlantId, meter.EquipmentId, meter.UtilityType,
                meter.Unit, meter.FdcParameterId, meter.ReadingMode, meter.ScaleFactor,
                meter.CostPerUnit, meter.CarbonPerUnit, meter.IsActive, actorId, savedAt));
            MeterSaveCommands.Add(new UtilityMeterSaveCommandRecord(
                idempotencyKey, requestHash, meter.MeterId, expectedVersion,
                meter.ConfigVersion, actorId, savedAt));
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<UtilityMeterConfigHistoryRecord>> GetMeterConfigHistoryAsync(
            string meterId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UtilityMeterConfigHistoryRecord>>(
                MeterConfigHistory.Where(x => x.MeterId == meterId).ToList());

        public Task<UtilityReadingRecord?> GetReadingAsync(string source, string sourceEventId, CancellationToken ct = default)
            => Task.FromResult(Readings.SingleOrDefault(x => x.Source == source && x.SourceEventId == sourceEventId));

        public Task<bool> TryAddReadingAsync(UtilityReadingRecord reading, CancellationToken ct = default)
        {
            if (Meter is null || !Meter.IsActive
                || Meter.ConfigVersion != reading.MeterConfigVersion
                || Readings.Any(x => x.Source == reading.Source && x.SourceEventId == reading.SourceEventId))
                return Task.FromResult(false);
            Readings.Add(reading);
            return Task.FromResult(true);
        }

        public Task<UtilityMeterEventRecord?> GetMeterEventAsync(
            string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(MeterEvents.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey));

        public Task<bool> TryAddMeterEventAsync(
            UtilityMeterEventRecord meterEvent, CancellationToken ct = default)
        {
            if (Meter is null || !Meter.IsActive
                || !Meter.ReadingMode.Equals("Cumulative", StringComparison.OrdinalIgnoreCase)
                || Meter.PlantId != meterEvent.PlantId || Meter.EquipmentId != meterEvent.EquipmentId
                || Meter.Unit != meterEvent.Unit
                || Meter.ConfigVersion != meterEvent.MeterConfigVersion
                || MeterEvents.Any(x => x.IdempotencyKey == meterEvent.IdempotencyKey))
                return Task.FromResult(false);
            MeterEvents.Add(meterEvent);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<UtilityMeterEventRecord>> GetMeterEventsAsync(
            string meterId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UtilityMeterEventRecord>>(
                MeterEvents.Where(x => x.MeterId == meterId
                                       && x.OccurredAt >= fromInclusive && x.OccurredAt < toExclusive)
                    .OrderBy(x => x.OccurredAt)
                    .ToList());

        public Task<IReadOnlyList<UtilityReadingRecord>> GetPeriodReadingsAsync(
            string meterId, DateTime from, DateTime to, bool includeBaseline, CancellationToken ct = default)
        {
            var rows = Readings
                .Where(x => x.MeterId == meterId && x.RecordedAt >= from && x.RecordedAt < to)
                .OrderBy(x => x.RecordedAt)
                .ToList();
            if (includeBaseline)
            {
                var baseline = Readings
                    .Where(x => x.MeterId == meterId && x.RecordedAt <= from
                                && x.Quality.Equals("Good", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.RecordedAt)
                    .FirstOrDefault();
                if (baseline is not null && rows.All(x => x.ReadingId != baseline.ReadingId))
                    rows.Insert(0, baseline);
            }
            return Task.FromResult<IReadOnlyList<UtilityReadingRecord>>(rows);
        }

        public Task SaveSummaryAsync(UtilitySummaryRecord summary, string actorId, CancellationToken ct = default)
        {
            Summaries.RemoveAll(x => x.SummaryId == summary.SummaryId);
            Summaries.Add(summary);
            return Task.CompletedTask;
        }
    }
}
