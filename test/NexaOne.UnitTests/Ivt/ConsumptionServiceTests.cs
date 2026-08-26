using FluentAssertions;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.UnitTests.Ivt;

public sealed class ConsumptionServiceTests
{
    private readonly MemoryRepository _repository = new();

    [Fact]
    public async Task Normalizes_to_six_decimals_and_rejects_sub_micro_quantity()
    {
        var service = new ConsumptionService(_repository);

        var recorded = await service.ConsumeAsync(Command() with { Quantity = 0.1234567m });
        var tooSmall = await service.ConsumeAsync(Command("small-key", "SMALL") with
        {
            Quantity = 0.0000004m,
        });

        recorded.IsSuccess.Should().BeTrue();
        recorded.Value.Quantity.Should().Be(0.123457m);
        tooSmall.IsFailure.Should().BeTrue();
        _repository.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Idempotency_hash_includes_actor_and_source_event_is_globally_unique()
    {
        var service = new ConsumptionService(_repository);
        var command = Command();
        (await service.ConsumeAsync(command)).IsSuccess.Should().BeTrue();

        var changedActor = await service.ConsumeAsync(command with { OperatorId = "other-user" });
        var newKeySameSource = await service.ConsumeAsync(
            command with { IdempotencyKey = "new-key", ConsumptionId = "CONSUME-2" });

        changedActor.IsFailure.Should().BeTrue();
        newKeySameSource.IsFailure.Should().BeTrue();
        _repository.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Reversal_replay_requires_the_same_full_request()
    {
        var service = new ConsumptionService(_repository);
        (await service.ConsumeAsync(Command())).IsSuccess.Should().BeTrue();
        var at = new DateTime(2026, 8, 26, 4, 5, 6, DateTimeKind.Utc);
        var reversal = new MaterialConsumptionReversalCommand(
            "REV-1", "reverse-key", "CONSUME-1", "operator correction", at,
            "MES", "operator-01", "corr-1");

        var first = await service.ReverseAsync(reversal);
        var replay = await service.ReverseAsync(reversal);
        var changedReason = await service.ReverseAsync(reversal with { Reason = "different" });

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.ConsumptionId.Should().Be(first.Value.ConsumptionId);
        changedReason.IsFailure.Should().BeTrue();
        _repository.Records.Should().HaveCount(2);
    }

    [Fact]
    public async Task Write_fails_closed_without_an_actor()
    {
        var previous = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        try
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
            var result = await new ConsumptionService(_repository).ConsumeAsync(
                Command() with { OperatorId = null });

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previous;
        }
    }

    private static MaterialConsumptionCommand Command(
        string idempotencyKey = "consume-key",
        string sourceEventId = "PLC-42") => new(
        "CONSUME-1", idempotencyKey, "PLANT01", "EQ01", "MATLOT01", "MAT01",
        1m, "kg", "Trace", new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc),
        "FDC", sourceEventId, OperatorId: "operator-01", CorrelationId: "corr-1",
        MetadataJson: "{\"binding\":\"B1\"}");

    private sealed class MemoryRepository : IConsumptionRepository
    {
        public List<ConsumptionRecord> Records { get; } = new();
        private decimal _balance = 100m;

        public Task<MaterialLotBalance?> GetLotAsync(
            string materialLotId,
            CancellationToken ct = default)
            => Task.FromResult<MaterialLotBalance?>(new(
                materialLotId, "MAT01", _balance, "kg", "InStock"));

        public Task<ConsumptionRecord?> GetByIdAsync(
            string consumptionId,
            CancellationToken ct = default)
            => Task.FromResult(Records.SingleOrDefault(x => x.ConsumptionId == consumptionId));

        public Task<ConsumptionRecord?> GetByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken ct = default)
            => Task.FromResult(Records.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey));

        public Task<ConsumptionRecord?> GetBySourceEventAsync(
            string sourceSystem,
            string sourceEventId,
            CancellationToken ct = default)
            => Task.FromResult(Records.SingleOrDefault(x =>
                x.ReversalOfId is null
                && x.SourceSystem == sourceSystem
                && x.SourceEventId == sourceEventId));

        public Task<bool> PersistAsync(ConsumptionRecord record, CancellationToken ct = default)
        {
            if (Records.Any(x => x.IdempotencyKey == record.IdempotencyKey
                                 || (x.ReversalOfId is null
                                     && x.SourceSystem == record.SourceSystem
                                     && x.SourceEventId == record.SourceEventId)))
                return Task.FromResult(false);
            _balance -= record.Quantity;
            Records.Add(record);
            return Task.FromResult(true);
        }

        public Task<bool> PersistReversalAsync(
            ConsumptionRecord original,
            ConsumptionRecord reversal,
            string reason,
            CancellationToken ct = default)
        {
            var index = Records.FindIndex(x =>
                x.ConsumptionId == original.ConsumptionId && x.Status == "Committed");
            if (index < 0) return Task.FromResult(false);
            Records[index] = Records[index] with { Status = "Reversed" };
            Records.Add(reversal);
            _balance += reversal.Quantity;
            return Task.FromResult(true);
        }
    }
}
