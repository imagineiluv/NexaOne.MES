using Microsoft.Extensions.Configuration;
using NexaOne.EST.Application.Oee;
using NexaOne.EST.Infrastructure;
using NexaFramework.Scheduling;

namespace NexaOne.UnitTests.Oee;

public sealed class OeeAggregationWorkerTests
{
    [Fact]
    public async Task Configuration_enables_worker_and_controls_schedule_interval()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Oee:Aggregation:Enabled"] = "true",
                ["Oee:Aggregation:IntervalSeconds"] = "17",
                ["Oee:Aggregation:LookbackDays"] = "2",
            })
            .Build();
        var scheduler = new Mock<IRecurringScheduler>();
        Func<CancellationToken, Task>? scheduledJob = null;
        scheduler.Setup(x => x.StartAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        scheduler.Setup(x => x.ScheduleRecurringAsync(
                It.IsAny<string>(), It.IsAny<TimeSpan>(),
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan, Func<CancellationToken, Task>, CancellationToken>(
                (_, _, job, _) => scheduledJob = job)
            .Returns(Task.CompletedTask);
        scheduler.Setup(x => x.StopAsync(It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var aggregator = new Mock<IOeeAggregator>();
        aggregator.Setup(x => x.AggregateRecentLocalDaysAsync(
                It.IsAny<DateTime>(), 2, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);
        var worker = new OeeAggregationWorker(
            scheduler.Object, aggregator.Object, configuration);

        await worker.StartAsync(CancellationToken.None);

        scheduler.Verify(x => x.StartAsync(It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(x => x.ScheduleRecurringAsync(
            "est-oee-aggregation",
            TimeSpan.FromSeconds(17),
            It.IsAny<Func<CancellationToken, Task>>(),
            It.IsAny<CancellationToken>()), Times.Once);
        scheduledJob.Should().NotBeNull();
        await scheduledJob!(CancellationToken.None);
        aggregator.Verify(x => x.AggregateRecentLocalDaysAsync(
            It.Is<DateTime>(value => value.Kind == DateTimeKind.Utc),
            2,
            It.IsAny<CancellationToken>()), Times.Once);

        await worker.StopAsync(CancellationToken.None);
        scheduler.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Once);
    }
}
