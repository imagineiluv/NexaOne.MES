using NexaLogic.Plc.Abstractions.Interfaces;
using NexaLogic.Plc.Abstractions.Models;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcRuntimeHealthPolicyTests
{
    [Fact]
    public void Endpoint_deadline_uses_the_configured_timeout_when_it_covers_the_polling_budget()
    {
        var endpoint = SlowEndpoint();

        var deadline = FdcCollectionWorker.CalculateStreamFreshnessDeadline(
            endpoint, TimeSpan.FromSeconds(90));

        deadline.Should().Be(TimeSpan.FromSeconds(90),
            "the operator-provided freshness timeout is the fail-closed deadline, not extra grace");
    }

    [Fact]
    public void Endpoint_timeout_below_polling_read_and_reconnect_budget_is_rejected()
    {
        var act = () => FdcCollectionWorker.CalculateStreamFreshnessDeadline(
            SlowEndpoint(), TimeSpan.FromSeconds(30));

        act.Should().Throw<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EP-SLOW*freshness timeout*below*poll/read/reconnect budget*00:01:07*");
    }

    [Fact]
    public void Endpoint_deadline_still_fails_closed_after_its_full_polling_budget()
    {
        var deadline = FdcCollectionWorker.CalculateStreamFreshnessDeadline(
            SlowEndpoint(), TimeSpan.FromSeconds(90));
        var health = new StubRuntimeHealth(
            generation: 7,
            isRunning: true,
            completedPollCount: 3,
            timeSinceLastCompletedPoll: deadline + TimeSpan.FromMilliseconds(1));

        var act = () => FdcCollectionWorker.EnsureRuntimeHealthFresh(
            "EP-SLOW", health, expectedGeneration: 7, deadline);

        act.Should().Throw<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EP-SLOW*stream is stale*endpoint deadline*");
    }

    [Theory]
    [InlineData(false, 7)]
    [InlineData(true, 8)]
    public void Stopped_or_replaced_subscription_generation_fails_closed(bool isRunning, long generation)
    {
        var health = new StubRuntimeHealth(
            generation,
            isRunning,
            completedPollCount: 1,
            timeSinceLastCompletedPoll: TimeSpan.Zero);

        var act = () => FdcCollectionWorker.EnsureRuntimeHealthFresh(
            "EP-GEN", health, expectedGeneration: 7, TimeSpan.FromSeconds(1));

        act.Should().Throw<FdcInterlockRuntimeUnavailableException>();
    }

    [Fact]
    public void Subscription_generation_change_during_freshness_snapshot_fails_closed()
    {
        var health = new ChangingGenerationRuntimeHealth();

        var act = () => FdcCollectionWorker.EnsureRuntimeHealthFresh(
            "EP-GEN-RACE", health, expectedGeneration: 7, TimeSpan.FromSeconds(1));

        act.Should().Throw<FdcInterlockRuntimeUnavailableException>()
            .WithMessage("*EP-GEN-RACE*generation changed while checking freshness*");
    }

    private static FdcEquipmentEndpoint SlowEndpoint() =>
        FdcEquipmentEndpoint.Create(
            "EP-SLOW",
            "EQ-1",
            "ModbusTcp",
            "tcp://plc:502",
            samplingIntervalMs: 60_000,
            tagMapPath: typeof(FdcRuntimeHealthPolicyTests).Assembly.Location,
            plcSettings: new FdcPlcEndpointSettings(
                ReadWriteTimeoutMs: 5_000,
                PollingDisconnectBackoffMs: 100,
                PollingMaxDisconnectBackoffMs: 2_000)).Value;

    private sealed class StubRuntimeHealth(
        long generation,
        bool isRunning,
        long completedPollCount,
        TimeSpan? timeSinceLastCompletedPoll) : IPlcCompletedPollSnapshotRuntimeHealth
    {
        public Task Completion { get; } = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously).Task;

        public long SubscriptionGeneration { get; } = generation;
        public bool IsRunning { get; } = isRunning;
        public long StartedPollCount { get; } = completedPollCount;
        public long CompletedPollCount { get; } = completedPollCount;
        public TimeSpan? TimeSinceLastCompletedPoll { get; } = timeSinceLastCompletedPoll;
        public DateTimeOffset? LastCompletedPollAt => null;
        public PlcCompletedPollSnapshot? LatestCompletedPollSnapshot => null;
    }

    private sealed class ChangingGenerationRuntimeHealth : IPlcCompletedPollSnapshotRuntimeHealth
    {
        private int _generationReadCount;

        public Task Completion => Task.CompletedTask;
        public long SubscriptionGeneration =>
            Interlocked.Increment(ref _generationReadCount) == 1 ? 7 : 8;
        public bool IsRunning => true;
        public long StartedPollCount => 1;
        public long CompletedPollCount => 1;
        public TimeSpan? TimeSinceLastCompletedPoll => TimeSpan.Zero;
        public DateTimeOffset? LastCompletedPollAt => null;
        public PlcCompletedPollSnapshot? LatestCompletedPollSnapshot => null;
    }
}
