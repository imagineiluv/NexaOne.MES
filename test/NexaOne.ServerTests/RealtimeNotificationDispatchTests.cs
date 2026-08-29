using NexaOne.Server.Realtime;
using Moq;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class RealtimeNotificationDispatchTests
{
    [Fact]
    public async Task Interlock_trigger_is_dispatched_as_interlock_fact_not_equipment_state()
    {
        var notifier = new Mock<IEesHubNotifier>();

        await RealtimeNotificationDispatch.DispatchAsync(
            notifier.Object,
            "InterlockTriggered",
            "EQ-001",
            "{\"EffectId\":\"FX-1\",\"RuleId\":\"RULE-1\",\"ParameterId\":\"TEMP01\",\"Action\":\"STOP\",\"Message\":\"over temp\",\"Value\":91.5}");

        notifier.Verify(n => n.NotifyInterlockTriggeredAsync(
            "EQ-001", "FX-1", "RULE-1", "TEMP01", "STOP", "over temp", 91.5m,
            It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.NotifyEquipmentStateChangedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Interlock_resolution_is_not_published_as_a_machine_state()
    {
        var notifier = new Mock<IEesHubNotifier>();

        await RealtimeNotificationDispatch.DispatchAsync(
            notifier.Object,
            "InterlockResolved",
            "EQ-001",
            "{\"EffectId\":\"FX-1\",\"RuleId\":\"RULE-1\",\"ParameterId\":\"TEMP01\",\"Value\":18.5,\"ResolvedAt\":\"2026-08-28T01:02:03Z\"}");

        notifier.Verify(n => n.NotifyInterlockResolvedAsync(
            "EQ-001", "FX-1", "RULE-1", "TEMP01", 18.5m,
            new DateTime(2026, 8, 28, 1, 2, 3, DateTimeKind.Utc),
            It.IsAny<CancellationToken>()), Times.Once);
        notifier.Verify(n => n.NotifyEquipmentStateChangedAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
