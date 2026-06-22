namespace NexaOne.Infrastructure.Persistence;

/// <summary>Transactional Outbox 행(ADR-002). 데이터 변경과 동일 트랜잭션에 기록되어 디스패처가 Kafka로 발행한다.</summary>
public sealed class OutboxMessage
{
    public long Id { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string AggregateId { get; init; } = string.Empty;
    public string Payload { get; init; } = string.Empty;
    public DateTime OccurredAt { get; init; }
    public DateTime? PublishedAt { get; init; }
    public int Attempts { get; init; }
    public DateTime? DeadLetteredAt { get; init; }
    public DateTime? NextAttemptAt { get; init; }
}
