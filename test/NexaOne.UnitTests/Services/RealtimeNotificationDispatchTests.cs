using NexaOne.API.Hubs;
using NexaOne.API.Services;

namespace NexaOne.UnitTests.Services;

/// <summary>RealtimeNotificationDispatch — 도메인 이벤트 유형 → SignalR 알림 매핑 고정(ADR-002 §2.5).
/// 버스가 기본 활성이라 이 매핑이 운영 기본 알림 경로다 — 이벤트 유형 오타/누락 시 세분 알림이 조용히
/// 대시보드 일괄갱신으로 떨어지는 회귀를 잡는다.</summary>
public sealed class RealtimeNotificationDispatchTests
{
    private static async Task<Mock<IEesHubNotifier>> DispatchAsync(string eventType, string aggregateId = "AGG", string payload = "P")
    {
        var mock = new Mock<IEesHubNotifier>();
        await RealtimeNotificationDispatch.DispatchAsync(mock.Object, eventType, aggregateId, payload);
        return mock;
    }

    [Fact]
    public async Task EquipmentStateChanged_routes_to_state_notification_with_payload()
    {
        var mock = await DispatchAsync("EquipmentStateChanged", "EQ-1", "RUNNING");
        mock.Verify(n => n.NotifyEquipmentStateChangedAsync("EQ-1", "RUNNING", It.IsAny<CancellationToken>()), Times.Once);
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("EquipmentAlarmRaised")]
    [InlineData("EquipmentAlarmCleared")]
    [InlineData("FdcAlarmRaised")]
    [InlineData("FdcAlarmCleared")]
    public async Task Alarm_events_route_to_alarm_update(string eventType)
    {
        var mock = await DispatchAsync(eventType);
        mock.Verify(n => n.NotifyAlarmUpdatedAsync(It.IsAny<CancellationToken>()), Times.Once);
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("WorkOrderStarted")]
    [InlineData("WorkOrderCompleted")]
    [InlineData("WorkOrderCancelled")]
    public async Task WorkOrder_events_route_to_workorder_update(string eventType)
    {
        var mock = await DispatchAsync(eventType);
        mock.Verify(n => n.NotifyWorkOrderUpdatedAsync(It.IsAny<CancellationToken>()), Times.Once);
        mock.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("LotTrackedIn")]            // POM 등 기타 lifecycle 이벤트는 대시보드 새로고침으로 폴백
    [InlineData("ProductionOrderStarted")]
    [InlineData("DeliveryOrderShipped")]
    [InlineData("UserRequestApproved")]
    [InlineData("UserAccountLocked")]
    [InlineData("SomethingUnknown")]
    public async Task Other_events_route_to_dashboard_refresh(string eventType)
    {
        var mock = await DispatchAsync(eventType);
        mock.Verify(n => n.NotifyDashboardRefreshAsync(It.IsAny<CancellationToken>()), Times.Once);
        mock.VerifyNoOtherCalls();
    }
}
