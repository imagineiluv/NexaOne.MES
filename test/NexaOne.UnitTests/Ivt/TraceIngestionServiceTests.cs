using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.UnitTests.Ivt;

public sealed class TraceIngestionServiceTests
{
    [Fact]
    public async Task EnqueueAsync_resumes_from_the_durable_cursor_and_snapshots_the_binding()
    {
        var at = new DateTime(2026, 8, 27, 1, 2, 3, DateTimeKind.Utc);
        var repository = new MemoryProjectionRepository
        {
            Bindings =
            [
                new TraceProjectionBinding(
                    "BIND-1", "PLANT-1", "EQ-1", "PARAM-1", "FEED-1",
                    "CounterDelta", 2m, null, "kg", at.AddHours(-1), null,
                    at, "COLLECT-0"),
            ],
        };
        var source = new MemoryTraceSource
        {
            Samples =
            [
                new FdcTraceSample(
                    "BIND-1", "COLLECT-1", "EQ-1", "PARAM-1", 12.345678m,
                    "Good", at.AddSeconds(1)),
            ],
        };

        var added = await new TraceIngestionService(source, repository).EnqueueAsync(100);

        added.Should().Be(1);
        source.LastScopes.Should().Equal(new FdcTraceReadScope(
            "BIND-1", "EQ-1", "PARAM-1", at.AddHours(-1), null, at, "COLLECT-0"));
        repository.Inbox.Should().Equal(new TraceProjectionItem(
            "BIND-1", "COLLECT-1", "PLANT-1", "EQ-1", "PARAM-1", "FEED-1",
            "CounterDelta", 2m, null, "kg", 12.345678m, "Good", at.AddSeconds(1)));
    }

    [Fact]
    public async Task EnqueueAsync_fails_closed_when_the_source_returns_an_unknown_scope()
    {
        var at = DateTime.UtcNow;
        var repository = new MemoryProjectionRepository
        {
            Bindings =
            [
                new TraceProjectionBinding(
                    "BIND-1", "PLANT-1", "EQ-1", "PARAM-1", "FEED-1",
                    "Direct", 1m, null, "kg", at.AddMinutes(-1), null, null, null),
            ],
        };
        var source = new MemoryTraceSource
        {
            Samples =
            [
                new FdcTraceSample(
                    "OTHER", "COLLECT-1", "EQ-1", "PARAM-1", 1m, "Good", at),
            ],
        };

        var act = () => new TraceIngestionService(source, repository).EnqueueAsync(100);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*unknown scope 'OTHER'*");
        repository.Inbox.Should().BeEmpty();
    }

    private sealed class MemoryTraceSource : IFdcTraceSource
    {
        public IReadOnlyList<FdcTraceSample> Samples { get; init; } = [];
        public IReadOnlyCollection<FdcTraceReadScope> LastScopes { get; private set; } = [];

        public Task<IReadOnlyList<FdcTraceSample>> ReadAsync(
            IReadOnlyCollection<FdcTraceReadScope> scopes,
            int maxCount,
            CancellationToken ct = default)
        {
            LastScopes = scopes;
            return Task.FromResult(Samples);
        }
    }

    private sealed class MemoryProjectionRepository : ITraceProjectionRepository
    {
        public IReadOnlyList<TraceProjectionBinding> Bindings { get; init; } = [];
        public List<TraceProjectionItem> Inbox { get; } = [];

        public Task<IReadOnlyList<TraceProjectionBinding>> GetSourceBindingsAsync(
            CancellationToken ct = default) => Task.FromResult(Bindings);

        public Task<int> AddToInboxAsync(
            IReadOnlyCollection<TraceProjectionItem> items,
            CancellationToken ct = default)
        {
            Inbox.AddRange(items);
            return Task.FromResult(items.Count);
        }

        public Task<IReadOnlyList<TraceProjectionItem>> GetPendingAsync(
            int batchSize,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TraceProjectionState?> GetStateAsync(
            string bindingId,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<MaterialFeedSession>> GetFeedSessionsAsync(
            string plantId,
            string equipmentId,
            string feedPointId,
            DateTime collectedAt,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task CompleteAsync(
            TraceProjectionItem item,
            TraceProjectionState? nextState,
            string status,
            string? consumptionId,
            string? detail,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task MarkErrorAsync(
            TraceProjectionItem item,
            string error,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task ReleaseLeaseAsync(
            string bindingId,
            string leaseOwnerId,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
