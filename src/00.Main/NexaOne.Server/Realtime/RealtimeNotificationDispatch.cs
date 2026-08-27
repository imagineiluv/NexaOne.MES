using System.Text.Json;

namespace NexaOne.Server.Realtime;

/// <summary>버스 구독자가 도메인 이벤트 유형을 알맞은 SignalR 알림으로 변환한다(ADR-002 §2.5 — 실시간 알림을 '버스 소비'로
/// 일원화). 상태 변경은 (설비,상태) 페이로드, 알람/작업지시는 갱신 신호, 그 외 lifecycle 이벤트는 대시보드 새로고침.
/// 이벤트 유형 추가 시 여기 한 곳만 보강한다. 폐기된 NexaOne.API에서 이식.</summary>
public static class RealtimeNotificationDispatch
{
    public static Task DispatchAsync(
        IEesHubNotifier notifier, string eventType, string aggregateId, string payload, CancellationToken ct = default)
    {
        if (eventType == "InterlockTriggered")
        {
            var detail = Deserialize<InterlockTriggeredPayload>(payload);
            return notifier.NotifyInterlockTriggeredAsync(
                aggregateId,
                detail?.EffectId ?? string.Empty,
                detail?.RuleId,
                detail?.ParameterId ?? string.Empty,
                detail?.Action ?? payload,
                detail?.Message ?? string.Empty,
                detail?.Value ?? 0m,
                ct);
        }

        if (eventType == "InterlockResolved")
        {
            var detail = Deserialize<InterlockResolvedPayload>(payload);
            return notifier.NotifyInterlockResolvedAsync(
                aggregateId,
                detail?.EffectId ?? string.Empty,
                detail?.RuleId,
                detail?.ParameterId ?? string.Empty,
                detail?.Value ?? 0m,
                detail?.ResolvedAt,
                ct);
        }

        return eventType switch
        {
            "EquipmentStateChanged" => notifier.NotifyEquipmentStateChangedAsync(aggregateId, payload, ct),
            "EquipmentAlarmRaised" or "EquipmentAlarmCleared" or "FdcAlarmRaised" or "FdcAlarmCleared"
                => notifier.NotifyAlarmUpdatedAsync(ct),
            "WorkOrderStarted" or "WorkOrderCompleted" or "WorkOrderCancelled"
                => notifier.NotifyWorkOrderUpdatedAsync(ct),
            _ => notifier.NotifyDashboardRefreshAsync(ct),
        };
    }

    private static T? Deserialize<T>(string payload) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(payload, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record InterlockTriggeredPayload(
        string EffectId,
        string? RuleId,
        string ParameterId,
        string Action,
        string Message,
        decimal Value);

    private sealed record InterlockResolvedPayload(
        string EffectId,
        string? RuleId,
        string ParameterId,
        decimal Value,
        DateTime? ResolvedAt);
}
