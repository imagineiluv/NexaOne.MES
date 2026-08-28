using Microsoft.Extensions.Configuration;
using NexaOne.FDC;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcModuleOptionsTests
{
    [Fact]
    public void Missing_configuration_keeps_all_workers_fail_safe_off()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        var options = FdcModuleOptions.FromConfiguration(configuration);

        options.Should().Be(new FdcModuleOptions(
            CollectionEnabled: false,
            EventTopic: "nexaone.events",
            RetentionEnabled: false,
            RetentionIntervalSeconds: 86_400,
            RetentionDays: 30,
            VirtualEventEnabled: false,
            VirtualEventIntervalSeconds: 30,
            InterlockActionTimeoutSeconds: 10,
            RuntimeHealthFreshnessTimeoutSeconds: 30,
            DriverCleanupTimeoutSeconds: 10));
    }

    [Fact]
    public void Configuration_controls_workers_without_exposing_policy_constants_in_spring_xml()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Worker:Fdc:Enabled"] = "true",
            ["Worker:Fdc:Topic"] = " fdc.events ",
            ["Worker:Fdc:Retention:Enabled"] = "true",
            ["Worker:Fdc:Retention:IntervalSeconds"] = "120",
            ["Worker:Fdc:Retention:RetentionDays"] = "45",
            ["Worker:Fdc:VirtualEvent:Enabled"] = "true",
            ["Worker:Fdc:VirtualEvent:IntervalSeconds"] = "15",
            ["Worker:Fdc:InterlockActionTimeoutSeconds"] = "7",
            ["Worker:Fdc:RuntimeHealth:FreshnessTimeoutSeconds"] = "19",
            ["Worker:Fdc:DriverCleanupTimeoutSeconds"] = "11",
        });

        var options = FdcModuleOptions.FromConfiguration(configuration);

        options.CollectionEnabled.Should().BeTrue();
        options.EventTopic.Should().Be("fdc.events");
        options.RetentionEnabled.Should().BeTrue();
        options.RetentionIntervalSeconds.Should().Be(120);
        options.RetentionDays.Should().Be(45);
        options.VirtualEventEnabled.Should().BeTrue();
        options.VirtualEventIntervalSeconds.Should().Be(15);
        options.InterlockActionTimeoutSeconds.Should().Be(7);
        options.RuntimeHealthFreshnessTimeoutSeconds.Should().Be(19);
        options.DriverCleanupTimeoutSeconds.Should().Be(11);
    }

    [Fact]
    public void Unsafe_intervals_are_clamped_and_shared_outbox_topic_is_the_fallback()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Worker:Fdc:Topic"] = "  ",
            ["Events:Outbox:Topic"] = " shared.events ",
            ["Worker:Fdc:Retention:IntervalSeconds"] = "0",
            ["Worker:Fdc:Retention:RetentionDays"] = "-1",
            ["Worker:Fdc:VirtualEvent:IntervalSeconds"] = "1",
        });

        var options = FdcModuleOptions.FromConfiguration(configuration);

        options.EventTopic.Should().Be("shared.events");
        options.RetentionIntervalSeconds.Should().Be(60);
        options.RetentionDays.Should().Be(1);
        options.VirtualEventIntervalSeconds.Should().Be(5);
    }

    [Theory]
    [InlineData("Worker:Fdc:InterlockActionTimeoutSeconds")]
    [InlineData("Worker:Fdc:RuntimeHealth:FreshnessTimeoutSeconds")]
    [InlineData("Worker:Fdc:DriverCleanupTimeoutSeconds")]
    public void Fail_closed_runtime_timeouts_must_be_positive(string key)
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?> { [key] = "0" });

        var act = () => FdcModuleOptions.FromConfiguration(configuration);

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{key}*positive*");
    }

    private static IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
