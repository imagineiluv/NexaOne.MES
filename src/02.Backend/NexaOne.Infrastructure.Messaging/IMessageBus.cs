namespace NexaOne.Infrastructure.Messaging;

/// <summary>
/// 도메인 이벤트 발행 백본 추상화(ADR-002). KafkaMessageBus가 구현하며, OutboxDispatcher가 이 추상화에
/// 의존해 발행한다 — 이로써 발행 경로가 단일 백본을 통과하고 디스패처를 단위 테스트할 수 있다.
/// </summary>
public interface IMessageBus
{
    Task PublishAsync(string topic, DomainEventMessage message, CancellationToken ct = default);
}
