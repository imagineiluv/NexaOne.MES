namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// Outbox 저장소(ADR-002). <see cref="EnqueueAsync"/>는 (이상적으로) 데이터 변경과 동일 트랜잭션에서
/// 호출되어 원자성을 보장하고, 디스패처가 <see cref="GetUnpublishedAsync"/>로 미발행 이벤트를 읽어
/// Kafka로 발행한 뒤 <see cref="MarkPublishedAsync"/>로 표시한다.
/// </summary>
public interface IOutboxRepository
{
    Task EnqueueAsync(string eventType, string module, string aggregateId, string payload, CancellationToken ct = default);

    /// <summary>발행 대상 이벤트를 ID 순으로 최대 <paramref name="batchSize"/>건 읽는다. 다음을 모두 제외한다:
    /// 이미 발행됨(PUBLISHED_AT), 데드레터됨(DEAD_LETTERED_AT), 시도 횟수 한계 도달(ATTEMPTS &gt;= maxAttempts),
    /// 백오프 대기 중(NEXT_ATTEMPT_AT &gt; now) — 영구 실패(포이즌) 메시지가 배치를 점유하거나 일시 실패가
    /// 즉시 재폭주하는 것을 방지한다.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetUnpublishedAsync(int batchSize, int maxAttempts, CancellationToken ct = default);
    Task MarkPublishedAsync(long id, CancellationToken ct = default);

    /// <summary>발행 실패를 기록한다. ATTEMPTS를 1 증가시키고 NEXT_ATTEMPT_AT를 지수 백오프(2^attempts초, 상한
    /// 적용) 뒤로 설정한다. 이번 실패로 attempts+1이 <paramref name="maxAttempts"/>에 도달하면 DEAD_LETTERED_AT를
    /// 설정해 단말 격리한다(이후 폴링에서 영구 제외).</summary>
    Task MarkFailedAsync(long id, int attempts, int maxAttempts, CancellationToken ct = default);

    /// <summary>데드레터된 메시지를 ID 순으로 최대 <paramref name="limit"/>건 읽는다(운영 조회·수동 조치용).</summary>
    Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(int limit, CancellationToken ct = default);

    /// <summary>데드레터 메시지를 재시도 가능 상태로 되돌린다(DEAD_LETTERED_AT·NEXT_ATTEMPT_AT 해제, ATTEMPTS=0).</summary>
    Task ResetForRetryAsync(long id, CancellationToken ct = default);

    /// <summary>발행 대기(미발행·비데드레터) 건수 — 적체 게이지용.</summary>
    Task<int> CountPendingAsync(CancellationToken ct = default);

    /// <summary>데드레터 건수 — 데드레터 게이지용.</summary>
    Task<int> CountDeadLetteredAsync(CancellationToken ct = default);
}
