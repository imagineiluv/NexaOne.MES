using FluentAssertions;
using NexaOne.EST.Application.Est;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.UnitTests.Oee;

public sealed class EquipmentOutputServiceTests
{
    private readonly MemoryRepository _repository = new();

    [Fact]
    public async Task Records_non_lot_carrier_output_and_replays_same_idempotency_key()
    {
        var service = new EquipmentOutputService(_repository);
        var command = Command();

        var first = await service.RecordAsync(command);
        var replay = await service.RecordAsync(command);

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.OutputEventId.Should().Be(first.Value.OutputEventId);
        _repository.Records.Should().ContainSingle();
        first.Value.CarrierId.Should().Be("CARRIER-001");
    }

    [Fact]
    public async Task Rejects_idempotency_key_reuse_with_different_quantity()
    {
        var service = new EquipmentOutputService(_repository);
        (await service.RecordAsync(Command())).IsSuccess.Should().BeTrue();

        var conflict = await service.RecordAsync(Command() with { TotalQuantity = 2m, GoodQuantity = 2m });

        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Type.Should().Be(NexaOne.Common.ErrorType.Conflict);
    }

    [Fact]
    public async Task Rejects_same_source_event_with_a_new_idempotency_key()
    {
        var service = new EquipmentOutputService(_repository);
        (await service.RecordAsync(Command())).IsSuccess.Should().BeTrue();

        var conflict = await service.RecordAsync(
            Command() with { IdempotencyKey = "different-key" });

        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("EST.Output.SourceEventConflict");
        _repository.Records.Should().ContainSingle();
    }

    [Theory]
    [InlineData(1, 1, 1)]
    [InlineData(0, 0, 0)]
    [InlineData(1, -1, 0)]
    public async Task Rejects_invalid_output_quantities(decimal total, decimal good, decimal defect)
    {
        var result = await new EquipmentOutputService(_repository).RecordAsync(
            Command() with { TotalQuantity = total, GoodQuantity = good, DefectQuantity = defect });

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
    }

    [Fact]
    public async Task Write_fails_closed_without_an_actor()
    {
        var previous = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        try
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
            var result = await new EquipmentOutputService(_repository).RecordAsync(
                Command() with { ActorId = null });

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previous;
        }
    }

    private static EquipmentOutputCommand Command() => new(
        "carrier-cleaned:EQ01:42",
        "PLANT01",
        "EQ01",
        "CarrierCleaned",
        1m,
        1m,
        0m,
        "EA",
        new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc),
        "EquipmentPlugin",
        SourceEventId: "PLC-CYCLE-42",
        CarrierId: "CARRIER-001",
        ActorId: "operator-01");

    private sealed class MemoryRepository : IEquipmentOutputRepository
    {
        public List<EquipmentOutputRecord> Records { get; } = new();

        public Task<EquipmentOutputRecord?> GetByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken ct = default)
            => Task.FromResult(Records.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey));

        public Task<EquipmentOutputRecord?> GetBySourceEventAsync(
            string source,
            string sourceEventId,
            CancellationToken ct = default)
            => Task.FromResult(Records.SingleOrDefault(x =>
                x.Source == source && x.SourceEventId == sourceEventId));

        public Task<bool> TryAddAsync(EquipmentOutputRecord record, CancellationToken ct = default)
        {
            if (Records.Any(x => x.IdempotencyKey == record.IdempotencyKey
                                 || (record.SourceEventId is not null
                                     && x.Source == record.Source
                                     && x.SourceEventId == record.SourceEventId)))
                return Task.FromResult(false);
            Records.Add(record);
            return Task.FromResult(true);
        }
    }
}
