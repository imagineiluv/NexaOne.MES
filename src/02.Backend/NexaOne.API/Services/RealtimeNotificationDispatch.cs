using NexaOne.API.Hubs;

namespace NexaOne.API.Services;

/// <summary>버스 구독자(인메모리/Kafka 공용)가 도메인 이벤트 유형을 알맞은 SignalR 알림으로 변환한다(ADR-002 §2.5 —
/// 실시간 알림을 '버스 소비'로 일원화). 컨트롤러 직접호출과 동일 충실도를 제공한다: 상태 변경은 (설비,상태) 페이로드,
/// 알람/작업지시는 갱신 신호, 그 외 lifecycle 이벤트는 대시보드 새로고침. 이벤트 유형 추가 시 여기 한 곳만 보강한다.</summary>
public static class RealtimeNotificationDispatch
{
    public static Task DispatchAsync(
        IEesHubNotifier notifier, string eventType, string aggregateId, string payload, CancellationToken ct = default)
        => eventType switch
        {
            "EquipmentStateChanged" => notifier.NotifyEquipmentStateChangedAsync(aggregateId, payload, ct),
            // FDC 수집 워커(ADR-006 Phase 2)가 버스로 발행하는 인터락 발동 — (설비,액션) 상태 변경 푸시로 전달.
            "InterlockTriggered" => notifier.NotifyEquipmentStateChangedAsync(aggregateId, payload, ct),
            "EquipmentAlarmRaised" or "EquipmentAlarmCleared" or "FdcAlarmRaised" or "FdcAlarmCleared"
                => notifier.NotifyAlarmUpdatedAsync(ct),
            "WorkOrderStarted" or "WorkOrderCompleted" or "WorkOrderCancelled"
                => notifier.NotifyWorkOrderUpdatedAsync(ct),
            _ => notifier.NotifyDashboardRefreshAsync(ct),
        };
}
