using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>EES_OUTBOX 저장소(ADR-002). 읽기는 QueryRepository(게이트웨이) 경유, 쓰기는 ServiceObjectProcessor(트랜잭션·감사필드) 경유.</summary>
public sealed class OutboxRepository : QueryRepository, IOutboxRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public OutboxRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public Task EnqueueAsync(string eventType, string module, string aggregateId, string payload, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO EES_OUTBOX
            (EVENT_TYPE, MODULE, AGGREGATE_ID, PAYLOAD, OCCURRED_AT, ATTEMPTS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@EventType, @Module, @AggregateId, @Payload, @OccurredAt, 0,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        return _processor.InsertAsync(sql, new
        {
            EventType = eventType,
            Module = module,
            AggregateId = aggregateId,
            Payload = payload,
            OccurredAt = DateTime.UtcNow
        }, ct);
    }

    // 지수 백오프 상한(초) — 2^attempts가 이 값을 넘지 않도록 한다(약 1시간). 2^12=4096s가 이미 1시간을 넘으므로 실효 상한.
    private const int BackoffCapSeconds = 3600;

    public Task<IReadOnlyList<OutboxMessage>> GetUnpublishedAsync(
        int batchSize, int maxAttempts, CancellationToken ct = default)
    {
        // 발행 대상 제외 조건: 발행됨 / 데드레터됨 / 시도 한계 도달 / 백오프 대기 중(NEXT_ATTEMPT_AT > now).
        // 영구 실패(포이즌) 메시지의 배치 점유와 일시 실패의 즉시 재폭주를 동시에 차단한다.
        const string baseSql = @"SELECT
                ID AS Id, EVENT_TYPE AS EventType, MODULE AS Module, AGGREGATE_ID AS AggregateId,
                PAYLOAD AS Payload, OCCURRED_AT AS OccurredAt, PUBLISHED_AT AS PublishedAt, ATTEMPTS AS Attempts,
                DEAD_LETTERED_AT AS DeadLetteredAt, NEXT_ATTEMPT_AT AS NextAttemptAt
            FROM EES_OUTBOX
            WHERE PUBLISHED_AT IS NULL AND DEAD_LETTERED_AT IS NULL AND ATTEMPTS < @maxAttempts
              AND (NEXT_ATTEMPT_AT IS NULL OR NEXT_ATTEMPT_AT <= @now)";
        var sql = _dialect.WrapPaged(baseSql, "ID", 0, batchSize);
        return QueryAsync<OutboxMessage>(sql, new { maxAttempts, now = DateTime.UtcNow }, ct);
    }

    public Task MarkPublishedAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"UPDATE EES_OUTBOX SET PUBLISHED_AT = @UpdatedAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt WHERE ID = @id";
        return _processor.UpdateAsync(sql, new { id }, ct);
    }

    public Task MarkFailedAsync(long id, int attempts, int maxAttempts, CancellationToken ct = default)
    {
        // 백오프·데드레터 시각은 C#에서 산정해 파라미터로 넘긴다(MSSQL DATEADD / SQLite datetime 방언 분기 회피).
        var now = DateTime.UtcNow;
        var backoffSeconds = Math.Min(Math.Pow(2, attempts), BackoffCapSeconds);
        var nextAttemptAt = now.AddSeconds(backoffSeconds);
        // 이번 실패로 시도 횟수가 한계에 도달하면 단말 격리(이후 폴링에서 영구 제외).
        DateTime? deadLetteredAt = attempts + 1 >= maxAttempts ? now : null;

        const string sql = @"UPDATE EES_OUTBOX SET ATTEMPTS = ATTEMPTS + 1,
            NEXT_ATTEMPT_AT = @nextAttemptAt, DEAD_LETTERED_AT = @deadLetteredAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt WHERE ID = @id";
        return _processor.UpdateAsync(sql, new { id, nextAttemptAt, deadLetteredAt }, ct);
    }

    public Task<IReadOnlyList<OutboxMessage>> GetDeadLetteredAsync(int limit, CancellationToken ct = default)
    {
        const string baseSql = @"SELECT
                ID AS Id, EVENT_TYPE AS EventType, MODULE AS Module, AGGREGATE_ID AS AggregateId,
                PAYLOAD AS Payload, OCCURRED_AT AS OccurredAt, PUBLISHED_AT AS PublishedAt, ATTEMPTS AS Attempts,
                DEAD_LETTERED_AT AS DeadLetteredAt, NEXT_ATTEMPT_AT AS NextAttemptAt
            FROM EES_OUTBOX
            WHERE DEAD_LETTERED_AT IS NOT NULL";
        var sql = _dialect.WrapPaged(baseSql, "ID", 0, limit);
        return QueryAsync<OutboxMessage>(sql, null, ct);
    }

    public Task ResetForRetryAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"UPDATE EES_OUTBOX SET ATTEMPTS = 0,
            DEAD_LETTERED_AT = NULL, NEXT_ATTEMPT_AT = NULL,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt WHERE ID = @id";
        return _processor.UpdateAsync(sql, new { id }, ct);
    }

    public Task<int> CountPendingAsync(CancellationToken ct = default)
    {
        const string sql = @"SELECT COUNT(*) FROM EES_OUTBOX
            WHERE PUBLISHED_AT IS NULL AND DEAD_LETTERED_AT IS NULL";
        return CountAsync(sql, null, ct);
    }

    public Task<int> CountDeadLetteredAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM EES_OUTBOX WHERE DEAD_LETTERED_AT IS NOT NULL";
        return CountAsync(sql, null, ct);
    }
}
