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

    public Task<IReadOnlyList<OutboxMessage>> GetUnpublishedAsync(int batchSize, CancellationToken ct = default)
    {
        const string baseSql = @"SELECT
                ID AS Id, EVENT_TYPE AS EventType, MODULE AS Module, AGGREGATE_ID AS AggregateId,
                PAYLOAD AS Payload, OCCURRED_AT AS OccurredAt, PUBLISHED_AT AS PublishedAt, ATTEMPTS AS Attempts
            FROM EES_OUTBOX
            WHERE PUBLISHED_AT IS NULL";
        var sql = _dialect.WrapPaged(baseSql, "ID", 0, batchSize);
        return QueryAsync<OutboxMessage>(sql, null, ct);
    }

    public Task MarkPublishedAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"UPDATE EES_OUTBOX SET PUBLISHED_AT = @UpdatedAt,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt WHERE ID = @id";
        return _processor.UpdateAsync(sql, new { id }, ct);
    }

    public Task MarkFailedAsync(long id, CancellationToken ct = default)
    {
        const string sql = @"UPDATE EES_OUTBOX SET ATTEMPTS = ATTEMPTS + 1,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt WHERE ID = @id";
        return _processor.UpdateAsync(sql, new { id }, ct);
    }
}
