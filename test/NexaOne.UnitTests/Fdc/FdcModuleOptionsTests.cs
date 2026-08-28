using Microsoft.Extensions.Configuration;
using NexaOne.FDC;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaFramework.Scheduling;

namespace NexaOne.UnitTests.Fdc;

public sealed class FdcModuleOptionsTests
{
    [Fact]
    public void Retention_guard_addition_preserves_legacy_public_constructor_ABI()
    {
        var moduleConstructorLengths = typeof(NexaOne.FDC.Module).GetConstructors()
            .Select(constructor => constructor.GetParameters().Length)
            .ToArray();
        moduleConstructorLengths.Should().Contain(8);
        moduleConstructorLengths.Should().Contain(9);
        typeof(FdcCollectDataRetentionWorker).GetConstructor(
        [
            typeof(IRecurringScheduler),
            typeof(IFdcCollectDataRepository),
            typeof(bool),
            typeof(int),
            typeof(int),
        ]).Should().NotBeNull();

        var act = () => new FdcCollectDataRetentionWorker(
            Mock.Of<IRecurringScheduler>(),
            Mock.Of<IFdcCollectDataRepository>(),
            enabled: true);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*legacy constructor*IVT retention guard*");
    }

    [Fact]
    public void Missing_configuration_keeps_all_workers_fail_safe_off()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>());

        var options = FdcModuleOptions.FromConfiguration(configuration);

        options.Should().Be(new FdcModuleOptions(
            CollectionEnabled: false,
            EventTopic: "nexaone.events",
            RetentionEnabled: false,
            RetentionBindingChangesQuiesced: false,
            RetentionIntervalSeconds: 86_400,
            RetentionDays: 30,
            VirtualEventEnabled: false,
            VirtualEventIntervalSeconds: 30,
            InterlockActionTimeoutSeconds: 10,
            RuntimeHealthFreshnessTimeoutSeconds: 30,
            DriverCleanupTimeoutSeconds: 10,
            RuntimeLease: new FdcLeaseOptions(
                "disabled", new string('0', 64),
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public void Configuration_controls_workers_without_exposing_policy_constants_in_spring_xml()
    {
        var configuration = BuildConfiguration(new Dictionary<string, string?>
        {
            ["Worker:Fdc:Enabled"] = "true",
            ["Worker:Fdc:Topic"] = " fdc.events ",
            ["Worker:Fdc:Retention:Enabled"] = "true",
            ["Worker:Fdc:Retention:BindingChangesQuiesced"] = "true",
            ["Worker:Fdc:Retention:IntervalSeconds"] = "120",
            ["Worker:Fdc:Retention:RetentionDays"] = "45",
            ["Worker:Fdc:VirtualEvent:Enabled"] = "true",
            ["Worker:Fdc:VirtualEvent:IntervalSeconds"] = "15",
            ["Worker:Fdc:InterlockActionTimeoutSeconds"] = "7",
            ["Worker:Fdc:RuntimeHealth:FreshnessTimeoutSeconds"] = "19",
            ["Worker:Fdc:DriverCleanupTimeoutSeconds"] = "11",
            ["Worker:Fdc:Ownership:OwnerId"] = " fdc-node-a ",
            ["Worker:Fdc:Ownership:ConfigRevisionSha256"] = new string('A', 64),
            ["Worker:Fdc:Ownership:LeaseDurationSeconds"] = "60",
            ["Worker:Fdc:Ownership:RenewIntervalSeconds"] = "20",
        });

        var options = FdcModuleOptions.FromConfiguration(configuration);

        options.CollectionEnabled.Should().BeTrue();
        options.EventTopic.Should().Be("fdc.events");
        options.RetentionEnabled.Should().BeTrue();
        options.RetentionBindingChangesQuiesced.Should().BeTrue();
        options.RetentionIntervalSeconds.Should().Be(120);
        options.RetentionDays.Should().Be(45);
        options.VirtualEventEnabled.Should().BeTrue();
        options.VirtualEventIntervalSeconds.Should().Be(15);
        options.InterlockActionTimeoutSeconds.Should().Be(7);
        options.RuntimeHealthFreshnessTimeoutSeconds.Should().Be(19);
        options.DriverCleanupTimeoutSeconds.Should().Be(11);
        options.RuntimeLease.OwnerId.Should().StartWith("fdc-node-a:");
        options.RuntimeLease.OwnerId.Length.Should().BeLessThanOrEqualTo(100);
        options.RuntimeLease.ConfigRevisionSha256.Should().Be(new string('a', 64));
        options.RuntimeLease.Duration.Should().Be(TimeSpan.FromSeconds(60));
        options.RuntimeLease.RenewInterval.Should().Be(TimeSpan.FromSeconds(20));

        FdcModuleOptions.FromConfiguration(configuration).RuntimeLease.OwnerId
            .Should().Be(options.RuntimeLease.OwnerId,
                "one process must reuse its process-start identity while each restart gets a new nonce");
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

    [Theory]
    [InlineData("Worker:Fdc:Ownership:OwnerId", "")]
    [InlineData("Worker:Fdc:Ownership:ConfigRevisionSha256", "not-a-digest")]
    public void Enabled_collection_requires_explicit_owner_and_canonical_config_digest(
        string invalidKey,
        string invalidValue)
    {
        var values = ValidOwnershipConfiguration();
        values[invalidKey] = invalidValue;

        var act = () => FdcModuleOptions.FromConfiguration(BuildConfiguration(values));

        act.Should().Throw<InvalidOperationException>().WithMessage("*Ownership*");
    }

    [Fact]
    public void Enabled_collection_rejects_a_renew_interval_greater_than_one_third_of_ttl()
    {
        var values = ValidOwnershipConfiguration();
        values["Worker:Fdc:Ownership:LeaseDurationSeconds"] = "30";
        values["Worker:Fdc:Ownership:RenewIntervalSeconds"] = "11";

        var act = () => FdcModuleOptions.FromConfiguration(BuildConfiguration(values));

        act.Should().Throw<InvalidOperationException>().WithMessage("*one third*");
    }

    private static Dictionary<string, string?> ValidOwnershipConfiguration() => new()
    {
        ["Worker:Fdc:Enabled"] = "true",
        ["Worker:Fdc:Ownership:OwnerId"] = "fdc-node-a",
        ["Worker:Fdc:Ownership:ConfigRevisionSha256"] = new string('a', 64),
        ["Worker:Fdc:Ownership:LeaseDurationSeconds"] = "30",
        ["Worker:Fdc:Ownership:RenewIntervalSeconds"] = "10",
    };

    private static IConfiguration BuildConfiguration(
        IReadOnlyDictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
