using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaOne.IVT.Infrastructure;
using NexaOne.ServiceContracts.Fdc;

namespace NexaOne.UnitTests.Ivt;

public sealed class TraceMaterialConsumptionWorkerTests
{
    [Fact]
    public async Task Canceled_batch_bounds_best_effort_lease_cleanup_and_preserves_cancellation()
    {
        var repository = new Mock<ITraceProjectionRepository>();
        repository.Setup(r => r.GetSourceBindingsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<TraceProjectionBinding>());
        repository.Setup(r => r.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new TraceProjectionItem(
                    "B1", "C1", "P1", "EQ1", "TAG1", "FEED1", "Direct",
                    1m, null, "kg", 1m, "Good", DateTime.UtcNow, "OWNER1"),
                new TraceProjectionItem(
                    "B2", "C2", "P1", "EQ1", "TAG2", "FEED2", "Direct",
                    1m, null, "kg", 1m, "Good", DateTime.UtcNow, "OWNER2"),
            });
        repository.Setup(r => r.ReleaseLeaseAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, string, CancellationToken>(
                (_, _, cleanupToken) => Task.Delay(Timeout.InfiniteTimeSpan, cleanupToken));

        var source = new Mock<IFdcTraceSource>();
        var consumptionRepository = new Mock<IConsumptionRepository>();
        var worker = new TraceMaterialConsumptionWorker(
            new TraceIngestionService(source.Object, repository.Object),
            repository.Object,
            new ConsumptionService(consumptionRepository.Object),
            enabled: true)
        {
            LeaseReleaseTimeout = TimeSpan.FromMilliseconds(100),
        };
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();

        var execution = worker.ProjectBatchAsync(callerCancellation.Token);

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => execution.WaitAsync(TimeSpan.FromSeconds(5)));
        exception.CancellationToken.Should().Be(callerCancellation.Token);
        repository.Verify(r => r.ReleaseLeaseAsync(
            "B1", "OWNER1", It.Is<CancellationToken>(token => token != callerCancellation.Token)), Times.Once);
        repository.Verify(r => r.ReleaseLeaseAsync("B2", "OWNER2", It.IsAny<CancellationToken>()), Times.Never,
            "the shared cleanup deadline must stop the remaining sequential release attempts");
    }
}
