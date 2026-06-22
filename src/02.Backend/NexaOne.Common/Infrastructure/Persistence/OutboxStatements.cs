using NexaOne.Common;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>ADR-002 트랜잭션 outbox — 애그리거트의 도메인 이벤트(IOutboxEvent)를 EES_OUTBOX INSERT 문장으로
/// 변환한다. 데이터 변경 문장과 같은 <see cref="ServiceObjectProcessor.ExecuteManyAsync"/> 배치에 넣어 '동일
/// 트랜잭션 발행'을 보장한다(데이터·이벤트가 함께 커밋되거나 함께 롤백). 모든 모듈 리포가 공유해 INSERT SQL
/// 중복을 없앤다 — 신규 애그리거트 확산은 이 헬퍼 호출 한 줄이면 된다.</summary>
public static class OutboxStatements
{
    // ID 자동증가(EES_OUTBOX 단일 INTEGER PK는 SQLite rowid 별칭이라 ID 생략 INSERT 가능) — raw 실행이라 감사값 명시.
    private const string InsertSql = @"
            INSERT INTO EES_OUTBOX
                (EVENT_TYPE, MODULE, AGGREGATE_ID, PAYLOAD, OCCURRED_AT, ATTEMPTS,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
                (@EventType, @Module, @AggregateId, @Payload, @OccurredAt, 0,
                 @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    /// <summary>도메인 이벤트들을 outbox INSERT (Sql, Param) 문장으로 변환한다. occurredAt/auditUser는 같은
    /// 트랜잭션 배치의 다른 문장과 시각·사용자를 일치시키도록 호출부가 한 번 결정해 전달한다.</summary>
    public static IEnumerable<(string Sql, object? Param)> For(
        IEnumerable<IOutboxEvent> events, string auditUser, DateTime occurredAt)
        => events.Select(ev => (InsertSql, (object?)new
        {
            ev.EventType, ev.Module, ev.AggregateId, ev.Payload,
            OccurredAt = occurredAt, CreatedBy = auditUser, CreatedAt = occurredAt,
            UpdatedBy = auditUser, UpdatedAt = occurredAt
        }));
}
