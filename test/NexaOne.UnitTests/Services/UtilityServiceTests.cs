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
            ActorId: "operator"))).IsSuccess.Should().BeTrue();

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

    [Fact]
    public async Task Meter_write_fails_closed_without_an_actor()
    {
        var previous = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        try
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
            var result = await new UtilityService(new MemoryRepository()).SaveMeterAsync(
                new UtilityMeterCommand("POWER-01", "Main", "P1", "Electricity", "kWh", "Delta"));

            result.IsFailure.Should().BeTrue();
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previous;
        }
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
        public List<UtilitySummaryRecord> Summaries { get; } = new();

        public Task<UtilityMeterRecord?> GetMeterAsync(string meterId, CancellationToken ct = default)
            => Task.FromResult(Meter?.MeterId == meterId ? Meter : null);

        public Task SaveMeterAsync(UtilityMeterRecord meter, string actorId, CancellationToken ct = default)
        {
            Meter = meter;
            return Task.CompletedTask;
        }

        public Task<UtilityReadingRecord?> GetReadingAsync(string source, string sourceEventId, CancellationToken ct = default)
            => Task.FromResult(Readings.SingleOrDefault(x => x.Source == source && x.SourceEventId == sourceEventId));

        public Task<bool> TryAddReadingAsync(UtilityReadingRecord reading, CancellationToken ct = default)
        {
            if (Readings.Any(x => x.Source == reading.Source && x.SourceEventId == reading.SourceEventId))
                return Task.FromResult(false);
            Readings.Add(reading);
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<UtilityReadingRecord>> GetPeriodReadingsAsync(
            string meterId, DateTime from, DateTime to, bool includeBaseline, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<UtilityReadingRecord>>(
                Readings.Where(x => x.MeterId == meterId && x.RecordedAt >= from && x.RecordedAt < to).ToList());

        public Task SaveSummaryAsync(UtilitySummaryRecord summary, string actorId, CancellationToken ct = default)
        {
            Summaries.RemoveAll(x => x.SummaryId == summary.SummaryId);
            Summaries.Add(summary);
            return Task.CompletedTask;
        }
    }
}
