using Microsoft.Extensions.Configuration;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaFramework.Scheduling;
using NexaOne.ERP;
using NexaOne.ERP.Application.Recurring;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.UnitTests.Erp;

public sealed class RecurringAutomationWorkerTests
{
    [Fact]
    public void Enabled_configuration_requires_a_principal_and_normalizes_interval_and_timezone()
    {
        var missing = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:Erp:Recurring:Enabled"] = "true",
        }).Build();
        var invalid = () => ErpModuleOptions.FromConfiguration(missing);
        invalid.Should().Throw<InvalidOperationException>().WithMessage("*PrincipalId*");

        var configured = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Worker:Erp:Recurring:Enabled"] = "true",
            ["Worker:Erp:Recurring:PrincipalId"] = " erp-monthly ",
            ["Worker:Erp:Recurring:IntervalSeconds"] = "1",
            ["Worker:Erp:Recurring:TimeZoneId"] = "UTC",
        }).Build();

        var options = ErpModuleOptions.FromConfiguration(configured);

        options.RecurringEnabled.Should().BeTrue();
        options.RecurringPrincipalId.Should().Be("erp-monthly");
        options.RecurringIntervalSeconds.Should().Be(60);
        options.RecurringTimeZone.Should().Be(TimeZoneInfo.Utc);
    }

    [Fact]
    public async Task Enabled_worker_uses_configured_clock_executes_each_due_rule_and_only_unschedules_itself()
    {
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var scope = new RecurringServicePrincipalScope("erp-monthly", tenant, organization, true, 1);
        var first = Rule("First", 1);
        var second = Rule("Second", 2);
        var automation = new Mock<IRecurringAutomationBridge>();
        automation.Setup(x => x.ListActiveScopesAsync("erp-monthly", 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BusinessPage<RecurringServicePrincipalScope>([scope], 1));
        automation.Setup(x => x.ListDueRulesAsync(
                "erp-monthly", tenant, organization, 1, new DateOnly(2026, 9, 30), 0, 100,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BusinessPage<RecurringRule>([first, second], 2));
        automation.Setup(x => x.ExecuteOccurrenceAsync(
                "erp-monthly", tenant, organization, 1, first.Id, new DateOnly(2026, 9, 1),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("first failed"));
        automation.Setup(x => x.ExecuteOccurrenceAsync(
                "erp-monthly", tenant, organization, 1, second.Id, new DateOnly(2026, 9, 1),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((RecurringExecution)null!);
        var scheduler = new Mock<IRecurringScheduler>();
        Func<CancellationToken, Task>? job = null;
        scheduler.Setup(x => x.StartAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        scheduler.Setup(x => x.ScheduleRecurringAsync(
                RecurringAutomationWorker.JobName, TimeSpan.FromMinutes(5),
                It.IsAny<Func<CancellationToken, Task>>(), It.IsAny<CancellationToken>()))
            .Callback<string, TimeSpan, Func<CancellationToken, Task>, CancellationToken>(
                (_, _, value, _) => job = value)
            .Returns(Task.CompletedTask);
        scheduler.Setup(x => x.UnscheduleAsync(
                RecurringAutomationWorker.JobName, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var worker = new RecurringAutomationWorker(
            scheduler.Object, automation.Object, true, "erp-monthly", TimeSpan.FromMinutes(5),
            TimeZoneInfo.Utc, new FixedTimeProvider(new DateTimeOffset(2026, 9, 30, 4, 0, 0, TimeSpan.Zero)));

        await worker.StartAsync(CancellationToken.None);
        job.Should().NotBeNull();
        await job!(CancellationToken.None);

        automation.Verify(x => x.ExecuteOccurrenceAsync(
            "erp-monthly", tenant, organization, 1, first.Id, new DateOnly(2026, 9, 1),
            It.IsAny<CancellationToken>()), Times.Once);
        automation.Verify(x => x.ExecuteOccurrenceAsync(
            "erp-monthly", tenant, organization, 1, second.Id, new DateOnly(2026, 9, 1),
            It.IsAny<CancellationToken>()), Times.Once,
            "one rule failure must not prevent another due rule from running");

        await worker.StopAsync(CancellationToken.None);
        scheduler.Verify(x => x.UnscheduleAsync(
            RecurringAutomationWorker.JobName, It.IsAny<CancellationToken>()), Times.Once);
        scheduler.Verify(x => x.StopAsync(It.IsAny<CancellationToken>()), Times.Never,
            "the scheduler is shared by module workers");
    }

    [Fact]
    public async Task Scope_calendar_uses_its_timezone_and_runs_bounded_catch_up_oldest_first()
    {
        var tenant = Guid.NewGuid();
        var organization = Guid.NewGuid();
        var scope = new RecurringServicePrincipalScope(
            "erp-monthly", tenant, organization, true, 7, "Asia/Seoul", 2);
        var rule = Rule("Monthly", 1);
        var automation = new Mock<IRecurringAutomationBridge>();
        automation.Setup(x => x.ListActiveScopesAsync("erp-monthly", 0, 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BusinessPage<RecurringServicePrincipalScope>([scope], 1));
        automation.Setup(x => x.ListDueRulesAsync(
                "erp-monthly", tenant, organization, 7, It.IsAny<DateOnly>(), 0, 100,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new BusinessPage<RecurringRule>([rule], 1));
        var months = new List<DateOnly>();
        automation.Setup(x => x.ExecuteOccurrenceAsync(
                "erp-monthly", tenant, organization, 7, rule.Id, It.IsAny<DateOnly>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, Guid, Guid, long, Guid, DateOnly, CancellationToken>(
                (_, _, _, _, _, month, _) => months.Add(month))
            .ReturnsAsync((RecurringExecution)null!);
        var worker = new RecurringAutomationWorker(
            Mock.Of<IRecurringScheduler>(), automation.Object, true, "erp-monthly", TimeSpan.FromHours(1),
            TimeZoneInfo.Utc, new FixedTimeProvider(new DateTimeOffset(2026, 9, 30, 15, 30, 0, TimeSpan.Zero)));

        await worker.RunAsync(CancellationToken.None);

        months.Should().Equal(
            new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1));
        automation.Verify(x => x.ListDueRulesAsync(
            "erp-monthly", tenant, organization, 7, new DateOnly(2026, 8, 31), 0, 100,
            It.IsAny<CancellationToken>()), Times.Once);
        automation.Verify(x => x.ListDueRulesAsync(
            "erp-monthly", tenant, organization, 7, new DateOnly(2026, 9, 30), 0, 100,
            It.IsAny<CancellationToken>()), Times.Once);
        automation.Verify(x => x.ListDueRulesAsync(
            "erp-monthly", tenant, organization, 7, new DateOnly(2026, 10, 1), 0, 100,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Disabled_worker_registers_no_job()
    {
        var scheduler = new Mock<IRecurringScheduler>();
        var worker = new RecurringAutomationWorker(
            scheduler.Object, Mock.Of<IRecurringAutomationBridge>(), false, string.Empty,
            TimeSpan.FromHours(1), TimeZoneInfo.Utc);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        scheduler.VerifyNoOtherCalls();
    }

    private static RecurringRule Rule(string name, int day)
        => new(Guid.NewGuid(), new("NexaOne.MES", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")),
            Guid.NewGuid(), Guid.NewGuid(),
            new(name, new(new DateOnly(2026, 1, 1), null, day),
                new RecurringIncomeTemplate(1m, Guid.NewGuid(), null, "KRW")),
            RecurringTarget.Income, "creator");

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
