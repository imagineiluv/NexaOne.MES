using NexaOne.API.Hubs;
using NexaOne.Infrastructure.Messaging;

namespace NexaOne.API.Services;

/// <summary>
/// 브로커리스(인메모리) Event Bus 구독자(ADR-002 토대). 시작 시 <see cref="InMemoryMessageBus"/>에
/// SignalR 푸시 핸들러를 등록한다 — Kafka 구독자(KafkaConsumerService)와 동일한 소비 의미를 인프로세스로
/// 제공한다(도메인 이벤트 → UI 갱신). Events:Outbox:Enabled && !Kafka:Enabled 일 때만 등록된다.
/// </summary>
public sealed class InMemoryBusSubscriberService : IHostedService
{
    private readonly InMemoryMessageBus _bus;
    private readonly IServiceScopeFactory _scopeFactory;

    public InMemoryBusSubscriberService(InMemoryMessageBus bus, IServiceScopeFactory scopeFactory)
    {
        _bus = bus;
        _scopeFactory = scopeFactory;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _bus.Subscribe(async (msg, ct) =>
        {
            using var scope = _scopeFactory.CreateScope();
            var notifier = scope.ServiceProvider.GetRequiredService<IEesHubNotifier>();
            if (msg.EventType == "EquipmentStateChanged")
                await notifier.NotifyEquipmentStateChangedAsync(msg.AggregateId, msg.Payload, ct);
            else
                await notifier.NotifyDashboardRefreshAsync(ct);
        });
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
