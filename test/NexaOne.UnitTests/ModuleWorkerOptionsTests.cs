using Microsoft.Extensions.Configuration;
using NexaOne.EMS;
using NexaOne.POM;
using NexaOne.SYS;

namespace NexaOne.UnitTests;

public sealed class ModuleWorkerOptionsTests
{
    [Fact]
    public void Missing_configuration_keeps_ems_and_sys_destructive_or_publishing_workers_off()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        EmsModuleOptions.FromConfiguration(configuration).Should().Be(
            new EmsModuleOptions(
                MaintenanceDueEnabled: false,
                MaintenanceDueIntervalSeconds: 3_600,
                EventTopic: "nexaone.events"));
        SysModuleOptions.FromConfiguration(configuration).Should().Be(
            new SysModuleOptions(
                LoginFailureRetentionEnabled: false,
                LoginFailureRetentionIntervalSeconds: 86_400,
                LoginFailureRetentionDays: 90));
        PomProjectionOptions.FromConfiguration(configuration).Should().Be(
            new PomProjectionOptions(
                Enabled: false,
                LeaseOwner: null,
                LeaseDurationSeconds: 120,
                PollIntervalMilliseconds: 2_000,
                BatchSize: 50));
    }

    [Fact]
    public void Configuration_maps_every_worker_constructor_parameter()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Worker:Ems:MaintenanceDue:Enabled"] = "true",
            ["Worker:Ems:MaintenanceDue:IntervalSeconds"] = "900",
            ["Worker:Ems:MaintenanceDue:Topic"] = " maintenance.events ",
            ["Worker:Sys:LoginFailureRetention:Enabled"] = "true",
            ["Worker:Sys:LoginFailureRetention:IntervalSeconds"] = "7200",
            ["Worker:Sys:LoginFailureRetention:RetentionDays"] = "120",
            ["Worker:Pom:WorkScopeProjection:Enabled"] = "false",
            ["Worker:Pom:WorkScopeProjection:LeaseOwner"] = " cleaner-mes-1 ",
            ["Worker:Pom:WorkScopeProjection:LeaseDurationSeconds"] = "300",
            ["Worker:Pom:WorkScopeProjection:PollIntervalMilliseconds"] = "500",
            ["Worker:Pom:WorkScopeProjection:BatchSize"] = "75",
        });

        EmsModuleOptions.FromConfiguration(configuration).Should().Be(
            new EmsModuleOptions(
                MaintenanceDueEnabled: true,
                MaintenanceDueIntervalSeconds: 900,
                EventTopic: "maintenance.events"));
        SysModuleOptions.FromConfiguration(configuration).Should().Be(
            new SysModuleOptions(
                LoginFailureRetentionEnabled: true,
                LoginFailureRetentionIntervalSeconds: 7_200,
                LoginFailureRetentionDays: 120));
        PomProjectionOptions.FromConfiguration(configuration).Should().Be(
            new PomProjectionOptions(
                Enabled: false,
                LeaseOwner: "cleaner-mes-1",
                LeaseDurationSeconds: 300,
                PollIntervalMilliseconds: 500,
                BatchSize: 75));
    }

    [Fact]
    public void Unsafe_intervals_are_clamped_and_ems_uses_the_shared_outbox_topic_fallback()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Worker:Ems:MaintenanceDue:IntervalSeconds"] = "0",
            ["Worker:Ems:MaintenanceDue:Topic"] = "  ",
            ["Events:Outbox:Topic"] = " shared.events ",
            ["Worker:Sys:LoginFailureRetention:IntervalSeconds"] = "-1",
            ["Worker:Sys:LoginFailureRetention:RetentionDays"] = "0",
            ["Worker:Pom:WorkScopeProjection:LeaseOwner"] = " ",
            ["Worker:Pom:WorkScopeProjection:LeaseDurationSeconds"] = "1",
            ["Worker:Pom:WorkScopeProjection:PollIntervalMilliseconds"] = "1",
            ["Worker:Pom:WorkScopeProjection:BatchSize"] = "9999",
        });

        var ems = EmsModuleOptions.FromConfiguration(configuration);
        var sys = SysModuleOptions.FromConfiguration(configuration);
        var pom = PomProjectionOptions.FromConfiguration(configuration);

        ems.MaintenanceDueEnabled.Should().BeFalse();
        ems.MaintenanceDueIntervalSeconds.Should().Be(60);
        ems.EventTopic.Should().Be("shared.events");
        sys.LoginFailureRetentionEnabled.Should().BeFalse();
        sys.LoginFailureRetentionIntervalSeconds.Should().Be(60);
        sys.LoginFailureRetentionDays.Should().Be(1);
        pom.LeaseOwner.Should().BeNull();
        pom.LeaseDurationSeconds.Should().Be(5);
        pom.PollIntervalMilliseconds.Should().Be(100);
        pom.BatchSize.Should().Be(500);
    }

    private static IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
