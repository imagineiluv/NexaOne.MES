using FluentAssertions;
using NexaOne.EST.Application.Est;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.UnitTests.Oee;

public sealed class EquipmentOutputServiceTests
{
    private readonly MemoryRepository _repository = new();

    [Fact]
    public async Task Records_non_lot_carrier_output_and_replays_same_idempotency_key()
    {
        var service = Service();
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
        var service = Service();
        (await service.RecordAsync(Command())).IsSuccess.Should().BeTrue();

        var conflict = await service.RecordAsync(Command() with { TotalQuantity = 2m, GoodQuantity = 2m });

        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Type.Should().Be(NexaOne.Common.ErrorType.Conflict);
    }

    [Fact]
    public async Task Rejects_same_source_event_with_a_new_idempotency_key()
    {
        var service = Service();
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
        var result = await Service().RecordAsync(
            Command() with { TotalQuantity = total, GoodQuantity = good, DefectQuantity = defect });

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
    }

    [Fact]
    public async Task Normalizes_quantities_to_database_scale_before_hash_and_persistence()
    {
        var service = Service();
        var first = await service.RecordAsync(Command() with
        {
            TotalQuantity = 1.23456m,
            GoodQuantity = 1.23456m,
        });
        var replay = await service.RecordAsync(Command() with
        {
            TotalQuantity = 1.23461m,
            GoodQuantity = 1.23461m,
        });

        first.IsSuccess.Should().BeTrue();
        first.Value.TotalQuantity.Should().Be(1.2346m);
        first.Value.GoodQuantity.Should().Be(1.2346m);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.OutputEventId.Should().Be(first.Value.OutputEventId);
        _repository.Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Rejects_quantities_that_do_not_balance_after_database_scale_normalization()
    {
        var result = await Service().RecordAsync(Command() with
        {
            TotalQuantity = 0.00012m,
            GoodQuantity = 0.00006m,
            DefectQuantity = 0.00006m,
        });

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        _repository.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_missing_occurrence_time_and_database_overflow()
    {
        var missingTime = await Service().RecordAsync(Command() with { OccurredAt = default });
        var overflow = await Service().RecordAsync(Command() with
        {
            TotalQuantity = 100000000000000m,
            GoodQuantity = 100000000000000m,
        });

        missingTime.IsFailure.Should().BeTrue();
        missingTime.Error.Code.Should().Contain(nameof(EquipmentOutputCommand.OccurredAt));
        overflow.IsFailure.Should().BeTrue();
        overflow.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        _repository.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Write_fails_closed_without_an_actor()
    {
        var previous = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        try
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
            var result = await Service().RecordAsync(
                Command() with { ActorId = null });

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previous;
        }
    }

    [Fact]
    public async Task Carrier_cleaned_requires_a_carrier_identifier()
    {
        var result = await Service().RecordAsync(Command() with { CarrierId = null });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Contain(nameof(EquipmentOutputCommand.CarrierId));
    }

    [Theory]
    [InlineData(true, "LOT-001")]
    [InlineData(false, "LOT-001")]
    public async Task Carrier_cleaned_rejects_process_lot_semantics(
        bool isLotOutput,
        string processLotId)
    {
        var result = await Service().RecordAsync(Command() with
        {
            IsLotOutput = isLotOutput,
            ProcessLotId = processLotId,
        });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Contain(nameof(EquipmentOutputCommand.ProcessLotId));
        _repository.Records.Should().BeEmpty();
    }

    [Fact]
    public async Task Rejects_equipment_from_another_plant_and_unknown_carrier()
    {
        var wrongPlant = await Service(new MasterDirectory(plantId: "PLANT02"))
            .RecordAsync(Command());
        var unknownCarrier = await Service(new MasterDirectory(carrierExists: false))
            .RecordAsync(Command());

        wrongPlant.IsFailure.Should().BeTrue();
        wrongPlant.Error.Code.Should().Contain(nameof(EquipmentOutputCommand.PlantId));
        unknownCarrier.IsFailure.Should().BeTrue();
        unknownCarrier.Error.Code.Should().Contain(nameof(EquipmentOutputCommand.CarrierId));
        _repository.Records.Should().BeEmpty();
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

    private EquipmentOutputService Service(IEquipmentOutputMasterDirectory? directory = null)
        => new(_repository, directory ?? new MasterDirectory());

    private sealed class MasterDirectory(
        string plantId = "PLANT01",
        bool equipmentValid = true,
        bool carrierExists = true) : IEquipmentOutputMasterDirectory
    {
        public Task<EquipmentOutputMasterScopeDto?> GetScopeAsync(
            string equipmentId,
            string? carrierId,
            CancellationToken ct = default)
            => Task.FromResult<EquipmentOutputMasterScopeDto?>(new(
                equipmentId, plantId, equipmentValid, carrierExists));
    }

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
