namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// Outbox 저장소(ADR-002). <see cref="EnqueueAsync"/>는 (이상적으로) 데이터 변경과 동일 트랜잭션에서
/// 호출되어 원자성을 보장하고, 디스패처가 <see cref="GetUnpublishedAsync"/>로 미발행 이벤트를 읽어
/// Kafka로 발행한 뒤 <see cref="MarkPublishedAsync"/>로 표시한다.
/// </summary>
public interface IOutboxRepository
{
    Task EnqueueAsync(string eventType, string module, string aggregateId, string payload, CancellationToken ct = default);
    Task<IReadOnlyList<OutboxMessage>> GetUnpublishedAsync(int batchSize, CancellationToken ct = default);
    Task MarkPublishedAsync(long id, CancellationToken ct = default);
    Task MarkFailedAsync(long id, CancellationToken ct = default);
}
