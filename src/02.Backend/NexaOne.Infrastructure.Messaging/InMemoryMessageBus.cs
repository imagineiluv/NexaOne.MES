using Microsoft.Extensions.Logging;

namespace NexaOne.Infrastructure.Messaging;

/// <summary>
/// 브로커 없는 인프로세스 <see cref="IMessageBus"/> 구현(ADR-002 토대). Kafka 없이 dev/단일노드/테스트에서
/// Outbox 디스패처의 발행 백본으로 동작한다 — 발행 시 등록된 인프로세스 구독자에게 즉시 동기 디스패치한다.
/// 운영 다중노드/내구성이 필요하면 Kafka:Enabled=true로 <see cref="KafkaMessageBus"/>를 쓴다.
/// </summary>
public sealed class InMemoryMessageBus : IMessageBus
{
    private readonly List<Func<DomainEventMessage, CancellationToken, Task>> _handlers = new();
    private readonly object _lock = new();
    private readonly ILogger<InMemoryMessageBus>? _logger;

    public InMemoryMessageBus(ILogger<InMemoryMessageBus>? logger = null) => _logger = logger;

    /// <summary>인프로세스 구독자 등록. 발행된 모든 메시지가 등록 순서대로 이 핸들러에 전달된다.</summary>
    public void Subscribe(Func<DomainEventMessage, CancellationToken, Task> handler)
    {
        lock (_lock) _handlers.Add(handler);
    }

    public async Task PublishAsync(string topic, DomainEventMessage message, CancellationToken ct = default)
    {
        Func<DomainEventMessage, CancellationToken, Task>[] snapshot;
        lock (_lock) snapshot = _handlers.ToArray();

        // 한 구독자의 실패가 다른 구독자/발행 자체를 막지 않도록 격리한다(디스패처는 발행 성공으로 간주).
        foreach (var handler in snapshot)
        {
            try
            {
                await handler(message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "InMemory 구독자 처리 실패 {EventType}/{AggregateId} (격리, 발행은 계속)",
                    message.EventType, message.AggregateId);
            }
        }

        _logger?.LogDebug("InMemory 발행 {EventType} → 구독자 {Count}건", message.EventType, snapshot.Length);
    }
}
