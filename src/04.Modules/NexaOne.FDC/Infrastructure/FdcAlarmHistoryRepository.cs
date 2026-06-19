using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcAlarmHistoryRepository : QueryRepository, IFdcAlarmHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public FdcAlarmHistoryRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(EST 알람 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyList<FdcAlarmHistory>> GetByEquipmentAsync(
        string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_ALARM_HISTORY
            WHERE EQUIPMENT_ID = @equipmentId AND OCCURRED_AT >= @from AND OCCURRED_AT <= @to
            ORDER BY OCCURRED_AT DESC";
        var rows = await QueryAsync<AlarmRow>(sql, new { equipmentId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcAlarmHistory>().ToList();
    }

    public async Task<IReadOnlyList<FdcAlarmHistory>> GetOpenAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_ALARM_HISTORY
            WHERE EQUIPMENT_ID = @equipmentId AND IS_CLEARED = 0
            ORDER BY OCCURRED_AT DESC";
        var rows = await QueryAsync<AlarmRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcAlarmHistory>().ToList();
    }

    private const string InsertSql = @"INSERT INTO FDC_ALARM_HISTORY
            (ALARM_ID, ALARM_CONFIG_ID, EQUIPMENT_ID, PARAMETER_ID, ALARM_LEVEL, TRIGGER_VALUE, MESSAGE,
             OCCURRED_AT, CLEARED_AT, IS_CLEARED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@AlarmId, @AlarmConfigId, @EquipmentId, @ParameterId, @AlarmLevel, @TriggerValue, @Message,
             @OccurredAt, @ClearedAt, @IsCleared,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE FDC_ALARM_HISTORY SET
            CLEARED_AT = @ClearedAt, IS_CLEARED = @IsCleared,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ALARM_ID = @AlarmId";

    public async Task AddAsync(FdcAlarmHistory history, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 INSERT(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.InsertAsync(InsertSql, AlarmRow.FromDomain(history), ct);
            return;
        }
        // ADR-002 활성: 알람 INSERT + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(history, InsertSql, isInsert: true, ct);
    }

    public async Task UpdateAsync(FdcAlarmHistory history, CancellationToken ct = default)
    {
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, AlarmRow.FromDomain(history), ct);
            return;
        }
        await PersistWithOutboxAsync(history, UpdateSql, isInsert: false, ct);
    }

    // 알람 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 알람 행의 감사 컬럼을
    // InsertAsync/UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(FdcAlarmHistory history, string sql, bool isInsert, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (sql, isInsert ? InsertParam(history, user, now) : UpdateParam(history, user, now)),
        };
        statements.AddRange(OutboxStatements.For(history.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        history.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters InsertParam(FdcAlarmHistory history, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters(AlarmRow.FromDomain(history));
        p.Add("CreatedBy", user);
        p.Add("CreatedAt", now);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private static Dapper.DynamicParameters UpdateParam(FdcAlarmHistory history, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("AlarmId", history.Id);
        p.Add("ClearedAt", history.ClearedAt);
        p.Add("IsCleared", history.IsCleared);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class AlarmRow
    {
        public string   AlarmId       { get; set; } = "";
        public string   AlarmConfigId { get; set; } = "";
        public string   EquipmentId   { get; set; } = "";
        public string   ParameterId   { get; set; } = "";
        public string   AlarmLevel    { get; set; } = "";
        public decimal  TriggerValue  { get; set; }
        public string   Message       { get; set; } = "";
        public DateTime OccurredAt    { get; set; }
        public DateTime? ClearedAt    { get; set; }
        public bool     IsCleared     { get; set; }

        // 감사 메타데이터(읽기경로 Restore 패턴) — SELECT * + MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑.
        public string    CreatedBy    { get; set; } = "";
        public DateTime  CreatedAt    { get; set; }
        public string?   UpdatedBy    { get; set; }
        public DateTime? UpdatedAt    { get; set; }

        // 읽기경로는 Restore로 전체 상태를 직접 복원한다 — Create+Clear 재생은 읽기 시마다 도메인 이벤트를
        // 유령 발행하므로(ADR-002) 금지. ClearedAt/IsCleared를 그대로 신뢰해 채운다.
        public FdcAlarmHistory ToDomain() =>
            FdcAlarmHistory.Restore(
                AlarmId, AlarmConfigId, EquipmentId, ParameterId, AlarmLevel, TriggerValue, Message,
                OccurredAt, ClearedAt, IsCleared,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static AlarmRow FromDomain(FdcAlarmHistory h) => new()
        {
            AlarmId       = h.Id,
            AlarmConfigId = h.AlarmConfigId,
            EquipmentId   = h.EquipmentId,
            ParameterId   = h.ParameterId,
            AlarmLevel    = h.AlarmLevel,
            TriggerValue  = h.TriggerValue,
            Message       = h.Message,
            OccurredAt    = h.OccurredAt,
            ClearedAt     = h.ClearedAt,
            IsCleared     = h.IsCleared
        };
    }
}
