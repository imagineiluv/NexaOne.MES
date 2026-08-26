using FluentAssertions;
using NexaOne.POM.Application.Lots;
using Xunit;

namespace NexaOne.UnitTests.Services;

public sealed class LotDispositionServiceTests
{
    [Fact]
    public async Task Exact_retry_replays_but_changed_payload_conflicts()
    {
        var repository = new MemoryRepository(Scope(3m));
        var service = new LotDispositionService(repository);
        var command = Command(quantity: 2m);

        var first = await service.RecordAsync(command);
        var replay = await service.RecordAsync(command);
        var conflict = await service.RecordAsync(command with { Reason = "different" });

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.DispositionId.Should().Be(first.Value.DispositionId);
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("POM.LotDisposition.IdempotencyConflict");
        repository.AddCount.Should().Be(1);
    }

    [Fact]
    public async Task Rejects_quantity_above_remaining_defect_evidence()
    {
        var repository = new MemoryRepository(Scope(1.5m));
        var result = await new LotDispositionService(repository)
            .RecordAsync(Command(quantity: 2m));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("POM.LotDisposition.QuantityExceeded");
        repository.AddCount.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SYSTEM-NOT-A-LOGIN-CLAIM-THAT-IS-LONGER-THAN-FIFTY-CHARACTERS")]
    public async Task Requires_valid_authenticated_actor(string actor)
    {
        var repository = new MemoryRepository(Scope(3m));
        var result = await new LotDispositionService(repository)
            .RecordAsync(Command(quantity: 1m) with { ActorId = actor });

        result.IsFailure.Should().BeTrue();
        repository.AddCount.Should().Be(0);
    }

    [Fact]
    public async Task Concurrent_allocation_loss_returns_conflict()
    {
        var repository = new MemoryRepository(Scope(3m)) { FailNextAdd = true };
        var result = await new LotDispositionService(repository)
            .RecordAsync(Command(quantity: 1m));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("POM.LotDisposition.ConcurrentAllocation");
    }

    [Fact]
    public async Task Requires_defect_code_when_defect_execution_is_supplied()
    {
        var repository = new MemoryRepository(Scope(3m));
        var result = await new LotDispositionService(repository)
            .RecordAsync(Command(quantity: 1m) with { DefectCode = null });

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(nameof(LotDispositionCommand.DefectCode));
        repository.AddCount.Should().Be(0);
    }

    [Fact]
    public async Task Changed_payload_with_field_separator_is_not_replayed_as_the_same_request()
    {
        var repository = new MemoryRepository(Scope(3m));
        var service = new LotDispositionService(repository);
        var firstCommand = Command(quantity: 1m) with
        {
            ReasonCode = "QUALITY\u001fconfirmed",
            Reason = "defect",
        };
        var changedCommand = firstCommand with
        {
            ReasonCode = "QUALITY",
            Reason = "confirmed\u001fdefect",
        };

        var first = await service.RecordAsync(firstCommand);
        var changed = await service.RecordAsync(changedCommand);

        first.IsSuccess.Should().BeTrue(first.IsFailure ? first.Error.Description : string.Empty);
        changed.IsFailure.Should().BeTrue();
        changed.Error.Code.Should().Be("POM.LotDisposition.IdempotencyConflict");
        repository.AddCount.Should().Be(1);
    }

    private static LotDispositionCommand Command(decimal quantity) => new(
        "PLANT01", "LOT01", "WO01", "CUT", "DEFEX01", "SCRATCH",
        "scrap", quantity, "QUALITY", "confirmed defect", "operator01",
        "DISP:LOT01:1", "mobile", "PDA-01", "TRACKOUT-01");

    private static LotDispositionScope Scope(decimal available) => new(
        "LOT01", "PLANT01", "WO01", "CUT", "DEFEX01", "SCRATCH",
        10m, 10m - available, 3m, Math.Max(0m, 3m - available));

    private sealed class MemoryRepository : ILotDispositionRepository
    {
        private readonly LotDispositionScope? _scope;
        private readonly Dictionary<string, LotDispositionRecord> _rows = new(StringComparer.Ordinal);

        public MemoryRepository(LotDispositionScope? scope) => _scope = scope;

        public int AddCount { get; private set; }
        public bool FailNextAdd { get; set; }

        public Task<LotDispositionRecord?> GetByIdempotencyKeyAsync(
            string idempotencyKey,
            CancellationToken ct = default) =>
            Task.FromResult(_rows.GetValueOrDefault(idempotencyKey));

        public Task<LotDispositionScope?> GetScopeAsync(
            string plantId,
            string lotId,
            string? workOrderId,
            string? processId,
            string? defectExecutionId,
            string? defectCode,
            CancellationToken ct = default) => Task.FromResult(_scope);

        public Task<bool> TryAddAsync(LotDispositionRecord record, CancellationToken ct = default)
        {
            if (FailNextAdd)
            {
                FailNextAdd = false;
                return Task.FromResult(false);
            }

            AddCount++;
            _rows.Add(record.IdempotencyKey, record);
            return Task.FromResult(true);
        }
    }
}
