using NexaFramework.Scheduling;
using NexaOne.FDC.Application.Fdc;
using NexaOne.ServiceContracts.Fdc;
using System.Runtime.CompilerServices;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcCollectDataRetentionWorkerTests
{
    [Fact]
    public async Task Purge_clamps_requested_cutoff_to_the_IVT_global_low_watermark()
    {
        var lowWatermark = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var (worker, scheduledJob, retentionRepository) = BuildWorker(
            guardSetup: guard => guard
                .Setup(x => x.GetLowWatermarkAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(lowWatermark));

        await worker.StartAsync(CancellationToken.None);
        await scheduledJob.Value!(CancellationToken.None);

        retentionRepository.PurgeCutoffs.Should().Equal(lowWatermark);
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Purge_fails_closed_when_the_IVT_guard_cannot_be_resolved_or_queried()
    {
        var (worker, scheduledJob, retentionRepository) = BuildWorker(
            guardSetup: guard => guard
                .Setup(x => x.GetLowWatermarkAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("IVT unavailable")));

        await worker.StartAsync(CancellationToken.None);
        await scheduledJob.Value!(CancellationToken.None);

        retentionRepository.PurgeCutoffs.Should().BeEmpty();
        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Enabled_retention_rejects_a_repository_without_the_atomic_purge_and_state_seams()
    {
        var act = () => new FdcCollectDataRetentionWorker(
            Mock.Of<IRecurringScheduler>(),
            Mock.Of<IFdcCollectDataRepository>(),
            Mock.Of<IFdcTraceRetentionGuard>(),
            enabled: true,
            bindingChangesQuiesced: true);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*implementing both durable retention purge and state contracts*");
    }

    [Fact]
    public void Enabled_retention_rejects_without_a_process_lifetime_binding_change_freeze()
    {
        var repository = new RetentionRepositoryStub();

        var act = () => new FdcCollectDataRetentionWorker(
            Mock.Of<IRecurringScheduler>(),
            repository,
            Mock.Of<IFdcTraceRetentionGuard>(),
            enabled: true,
            bindingChangesQuiesced: false);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*BindingChangesQuiesced=true*entire process lifetime*");
    }

    private static (
        FdcCollectDataRetentionWorker Worker,
        StrongBox<Func<CancellationToken, Task>?> ScheduledJob,
        RetentionRepositoryStub RetentionRepository) BuildWorker(
        Action<Mock<IFdcTraceRetentionGuard>> guardSetup)
    {
        var scheduledJob = new StrongBox<Func<CancellationToken, Task>?>();
        var scheduler = new Mock<IRecurringScheduler>();
        scheduler.Setup(x => x.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        scheduler.Setup(x => x.ScheduleRecurringAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan, Func<CancellationToken, Task>, CancellationToken>(
                (_, _, job, _) => scheduledJob.Value = job)
            .Returns(Task.CompletedTask);
        scheduler.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var repository = new RetentionRepositoryStub();
        var guard = new Mock<IFdcTraceRetentionGuard>();
        guardSetup(guard);

        var worker = new FdcCollectDataRetentionWorker(
            scheduler.Object,
            repository,
            guard.Object,
            enabled: true,
            bindingChangesQuiesced: true,
            intervalSeconds: 60,
            retentionDays: 30);
        return (worker, scheduledJob, repository);
    }

    private sealed class RetentionRepositoryStub :
        IFdcCollectDataRepository,
        IFdcCollectDataRetentionRepository,
        IFdcTraceRetentionStateRepository
    {
        public List<DateTime> PurgeCutoffs { get; } = [];

        Task<FdcRetentionPurgeResult> IFdcCollectDataRetentionRepository.PurgeOlderThanAsync(
            DateTime cutoff,
            CancellationToken ct)
        {
            PurgeCutoffs.Add(cutoff);
            return Task.FromResult(new FdcRetentionPurgeResult(
                DeletedRows: 0,
                BatchLimitReached: false,
                OldestRemainingCollectedAt: null,
                Elapsed: TimeSpan.Zero));
        }

        public Task<FdcTraceRetentionState> GetTraceRetentionStateAsync(CancellationToken ct = default) =>
            Task.FromResult(new FdcTraceRetentionState(DateTime.UnixEpoch));

        public Task<IReadOnlyList<NexaOne.FDC.Domain.FdcCollectData>> GetByParameterAsync(
            string parameterId, DateTime from, DateTime to, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NexaOne.FDC.Domain.FdcCollectData>>([]);

        public Task<IReadOnlyList<NexaOne.FDC.Domain.FdcCollectData>> GetLatestAsync(
            string parameterId, int limit, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NexaOne.FDC.Domain.FdcCollectData>>([]);

        public Task<IReadOnlyList<NexaOne.FDC.Domain.FdcCollectData>> GetTraceAsync(
            string equipmentId,
            string parameterId,
            DateTime effectiveFrom,
            DateTime? effectiveTo,
            DateTime? afterCollectedAt,
            string? afterCollectId,
            int limit,
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NexaOne.FDC.Domain.FdcCollectData>>([]);

        public Task AddAsync(
            NexaOne.FDC.Domain.FdcCollectData data,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task AddBatchAsync(
            IEnumerable<NexaOne.FDC.Domain.FdcCollectData> data,
            CancellationToken ct = default) => Task.CompletedTask;

#pragma warning disable CS0618 // Stub preserves the legacy ABI while production rejects this path.
        public Task<int> DeleteOlderThanAsync(DateTime cutoff, CancellationToken ct = default) =>
            Task.FromException<int>(new InvalidOperationException("Legacy deletion is disabled."));
#pragma warning restore CS0618
    }
}
