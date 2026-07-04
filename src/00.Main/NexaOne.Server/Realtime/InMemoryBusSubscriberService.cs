using NexaOne.Infrastructure.Messaging;

namespace NexaOne.Server.Realtime;

/// <summary>브로커리스(인메모리) Event Bus 구독자(ADR-002 토대). 시작 시 <see cref="InMemoryMessageBus"/>에 SignalR 푸시
/// 핸들러를 등록한다 — 도메인 이벤트(FDC 수집·설비 상태/알람·작업지시 등) → UI 실시간 갱신. 폐기된 NexaOne.API에서
/// 통합 호스트로 이식(회귀 복원). 루트 Spring 컨텍스트의 messageBus(InMemoryMessageBus)를 Program.cs가 주입한다.</summary>
public sealed class InMemoryBusSubscriberService : IHostedService
{
    private readonly InMemoryMessageBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ScreenRefreshNotifier? _screenRefresh;
    private readonly RealtimeAlertFeed? _alertFeed;

    public InMemoryBusSubscriberService(
        InMemoryMessageBus bus, IServiceScopeFactory scopeFactory,
        ScreenRefreshNotifier? screenRefresh = null, RealtimeAlertFeed? alertFeed = null)
    {
        _bus = bus;
        _scopeFactory = scopeFactory;
        _screenRefresh = screenRefresh;
        _alertFeed = alertFeed;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _bus.Subscribe(async (msg, ct) =>
        {
            using var scope = _scopeFactory.CreateScope();
            var notifier = scope.ServiceProvider.GetRequiredService<IEesHubNotifier>();
            // 이벤트 유형별 세분 알림(상태/알람/작업지시/대시보드) — 컨트롤러 직접호출과 동일 충실도(ADR-002 §2.5).
            await RealtimeNotificationDispatch.DispatchAsync(notifier, msg.EventType, msg.AggregateId, msg.Payload, ct);

            // 실시간 v3 — 라이브 메타 화면(Blazor 회로) 즉시 재조회 팬아웃(SignalR 외 인프로세스 경로).
            if (_screenRefresh is not null)
                await _screenRefresh.NotifyAsync();

            // P3-20 — 알림성 이벤트(알람/인터락/작업지시)는 셸 알림 센터(벨)로도 팬아웃.
            if (_alertFeed is not null && RealtimeAlertFeed.ToAlert(msg.EventType, msg.AggregateId) is { } alert)
                await _alertFeed.PublishAsync(alert);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
