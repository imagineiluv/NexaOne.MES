namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// Outbox 저장소(ADR-002). <see cref="EnqueueAsync"/>는 (이상적으로) 데이터 변경과 동일 트랜잭션에서
/// 호출되어 원자성을 보장하고, 디스패처가 <see cref="GetUnpublishedAsync"/>로 미발행 이벤트를 읽어
/// Kafka로 발행한 뒤 <see cref="MarkPublishedAsync"/>로 표시한다.
/// </summary>
public interface IOutboxRepository
{
    Task EnqueueAsync(string eventType, string module, string aggregateId, string payload, CancellationToken ct = default);

    /// <summary>미발행 이벤트를 OCCURRED 순으로 최대 <paramref name="batchSize"/>건 읽는다. 다만
    /// 시도 횟수가 <paramref name="maxAttempts"/> 이상인 행은 제외한다(데드레터) — 영구 실패(포이즌)
    /// 메시지가 매 폴링마다 재시도되어 배치를 점유하고 정상 메시지를 가로막는 것을 방지한다.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetUnpublishedAsync(int batchSize, int maxAttempts, CancellationToken ct = default);
    Task MarkPublishedAsync(long id, CancellationToken ct = default);
    Task MarkFailedAsync(long id, CancellationToken ct = default);
}
