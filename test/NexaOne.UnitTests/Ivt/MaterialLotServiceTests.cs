using FluentAssertions;
using NexaOne.Common;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.UnitTests.Ivt;

public sealed class MaterialLotServiceTests
{
    private readonly MemoryRepository _repository = new();

    [Fact]
    public async Task One_contract_executes_receive_move_hold_release_scrap_and_adjustment()
    {
        var service = new MaterialLotService(_repository);

        var receive = await service.ExecuteAsync(Command(MaterialLotOperations.Receive, 0) with
        {
            MaterialId = "MAT-01", LotNumber = "SUPPLIER-LOT-1", Quantity = 10m,
            Unit = "kg", Location = "WAREHOUSE",
        });
        var move = await service.ExecuteAsync(Command(MaterialLotOperations.Move, 1) with
        {
            TransactionId = "TX-2", IdempotencyKey = "KEY-2", SourceEventId = "EV-2",
            Location = "LINE-01",
        });
        var hold = await service.ExecuteAsync(Command(MaterialLotOperations.Hold, 2) with
        {
            TransactionId = "TX-3", IdempotencyKey = "KEY-3", SourceEventId = "EV-3",
            Reason = "quality inspection",
        });
        var release = await service.ExecuteAsync(Command(MaterialLotOperations.Release, 3) with
        {
            TransactionId = "TX-4", IdempotencyKey = "KEY-4", SourceEventId = "EV-4",
        });
        var scrap = await service.ExecuteAsync(Command(MaterialLotOperations.Scrap, 4) with
        {
            TransactionId = "TX-5", IdempotencyKey = "KEY-5", SourceEventId = "EV-5",
            Quantity = 3m, Reason = "damaged",
        });
        var adjustment = await service.ExecuteAsync(Command(MaterialLotOperations.Adjustment, 5) with
        {
            TransactionId = "TX-6", IdempotencyKey = "KEY-6", SourceEventId = "EV-6",
            Quantity = 2m, Reason = "cycle count",
        });

        new[] { receive, move, hold, release, scrap, adjustment }
            .Should().OnlyContain(result => result.IsSuccess);
        _repository.Lot.Should().Be(new MaterialLotState(
            "LOT-01", "MAT-01", "SUPPLIER-LOT-1", "LINE-01", 9m, "kg", "InStock", 6));
        _repository.Transactions.Should().HaveCount(6);
        _repository.Transactions.Select(x => x.BalanceDelta)
            .Should().Equal(10m, 0m, 0m, 0m, -3m, 2m);
    }

    [Fact]
    public async Task Replay_requires_identical_canonical_request_and_actor_is_fail_closed()
    {
        var service = new MaterialLotService(_repository);
        var command = Command(MaterialLotOperations.Receive, 0) with
        {
            MaterialId = "MAT-01", Quantity = 1.1234567m, Unit = "kg", Location = "STORE",
        };

        var first = await service.ExecuteAsync(command);
        var replay = await service.ExecuteAsync(command);
        var changed = await service.ExecuteAsync(command with { ActorId = "other-user" });
        var previousActor = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = null;
        Result<MaterialLotEventDto> noActor;
        try
        {
            noActor = await service.ExecuteAsync(command with
            {
                TransactionId = "TX-NO-ACTOR", IdempotencyKey = "NO-ACTOR",
                SourceEventId = "NO-ACTOR", ActorId = null,
            });
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previousActor;
        }

        first.IsSuccess.Should().BeTrue();
        first.Value.BalanceAfter.Should().Be(1.123457m);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        changed.Error.Type.Should().Be(ErrorType.Conflict);
        noActor.Error.Type.Should().Be(ErrorType.Validation);
        _repository.Transactions.Should().ContainSingle();
    }

    [Fact]
    public async Task Stale_version_and_invalid_state_do_not_append_a_transaction()
    {
        var service = new MaterialLotService(_repository);
        await service.ExecuteAsync(Command(MaterialLotOperations.Receive, 0) with
        {
            MaterialId = "MAT-01", Quantity = 5m, Unit = "kg", Location = "STORE",
        });
        await service.ExecuteAsync(Command(MaterialLotOperations.Hold, 1) with
        {
            TransactionId = "TX-H", IdempotencyKey = "KEY-H", SourceEventId = "EV-H",
            Reason = "inspection",
        });

        var stale = await service.ExecuteAsync(Command(MaterialLotOperations.Move, 1) with
        {
            TransactionId = "TX-M", IdempotencyKey = "KEY-M", SourceEventId = "EV-M",
            Location = "LINE",
        });
        var duplicateHold = await service.ExecuteAsync(Command(MaterialLotOperations.Hold, 2) with
        {
            TransactionId = "TX-H2", IdempotencyKey = "KEY-H2", SourceEventId = "EV-H2",
            Reason = "again",
        });

        stale.Error.Type.Should().Be(ErrorType.Conflict);
        duplicateHold.Error.Type.Should().Be(ErrorType.Conflict);
        _repository.Transactions.Should().HaveCount(2);
    }

    private static MaterialLotCommand Command(string operation, int expectedVersion) => new(
        "TX-1", "KEY-1", operation, "LOT-01", expectedVersion,
        new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc), "MES", "EV-1",
        ActorId: "operator-01", CorrelationId: "CORR-1");

    private sealed class MemoryRepository : IMaterialLotRepository
    {
        public MaterialLotState? Lot { get; private set; }
        public List<MaterialLotTransaction> Transactions { get; } = new();

        public Task<MaterialLotState?> GetLotAsync(string lotId, CancellationToken ct = default)
            => Task.FromResult(Lot?.LotId == lotId ? Lot : null);

        public Task<MaterialLotTransaction?> GetByIdempotencyKeyAsync(
            string idempotencyKey, CancellationToken ct = default)
            => Task.FromResult(Transactions.SingleOrDefault(x => x.IdempotencyKey == idempotencyKey));

        public Task<MaterialLotTransaction?> GetBySourceEventAsync(
            string sourceSystem, string sourceEventId, CancellationToken ct = default)
            => Task.FromResult(Transactions.SingleOrDefault(x =>
                x.SourceSystem == sourceSystem && x.SourceEventId == sourceEventId));

        public Task<bool> TryReceiveAsync(MaterialLotTransaction record, CancellationToken ct = default)
        {
            if (Lot is not null || IsDuplicate(record)) return Task.FromResult(false);
            Lot = new MaterialLotState(record.LotId, record.MaterialId, record.LotNumber,
                record.ToLocation, record.BalanceAfter, record.Unit!, record.ResultStatus,
                record.ResultVersion);
            Transactions.Add(record);
            return Task.FromResult(true);
        }

        public Task<bool> TryApplyAsync(MaterialLotTransaction record, CancellationToken ct = default)
        {
            if (Lot is null || IsDuplicate(record) || Lot.Version != record.ExpectedVersion ||
                Lot.Balance != record.BalanceBefore || Lot.Status != record.PreviousStatus ||
                Lot.Location != record.FromLocation) return Task.FromResult(false);
            Lot = Lot with
            {
                Location = record.ToLocation,
                Balance = record.BalanceAfter,
                Status = record.ResultStatus,
                Version = record.ResultVersion,
            };
            Transactions.Add(record);
            return Task.FromResult(true);
        }

        private bool IsDuplicate(MaterialLotTransaction record) => Transactions.Any(x =>
            x.IdempotencyKey == record.IdempotencyKey ||
            x.SourceSystem == record.SourceSystem && x.SourceEventId == record.SourceEventId);
    }
}
