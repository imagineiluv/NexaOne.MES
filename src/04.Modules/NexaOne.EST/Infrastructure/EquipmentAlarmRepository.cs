using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EST.Infrastructure;

public sealed class EquipmentAlarmRepository : QueryRepository, IEquipmentAlarmRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public EquipmentAlarmRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<EquipmentAlarm?> GetByIdAsync(string alarmId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EST_EQUIPMENT_ALARM WHERE ALARM_ID = @alarmId";
        var row = await QueryFirstOrDefaultAsync<AlarmRow>(sql, new { alarmId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<EquipmentAlarm>> GetByEquipmentAsync(string equipmentId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM EST_EQUIPMENT_ALARM
            WHERE EQUIPMENT_ID = @equipmentId
              AND (@from IS NULL OR OCCURRED_AT >= @from)
              AND (@to IS NULL OR OCCURRED_AT <= @to)";
        var rows = await QueryAsync<AlarmRow>(sql, new { equipmentId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<EquipmentAlarm>().ToList();
    }

    public async Task<IReadOnlyList<EquipmentAlarm>> GetActiveAlarmsAsync(string plantId, CancellationToken ct = default)
    {
        const string sql = @"SELECT a.* FROM EST_EQUIPMENT_ALARM a
            INNER JOIN MDM_EQUIPMENT e ON e.EQUIPMENT_ID = a.EQUIPMENT_ID
            WHERE e.PLANT_ID = @plantId AND a.CLEARED_AT IS NULL";
        var rows = await QueryAsync<AlarmRow>(sql, new { plantId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<EquipmentAlarm>().ToList();
    }

    public async Task<int> GetActiveAlarmCountAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM EST_EQUIPMENT_ALARM WHERE CLEARED_AT IS NULL";
        return await CountAsync(sql, null, ct);
    }

    private const string InsertSql = @"INSERT INTO EST_EQUIPMENT_ALARM
            (ALARM_ID, EQUIPMENT_ID, ALARM_CODE, ALARM_NAME, ALARM_LEVEL, OCCURRED_AT,
             CLEARED_AT, ELAPSED_SECONDS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@AlarmId, @EquipmentId, @AlarmCode, @AlarmName, @AlarmLevel, @OccurredAt,
             @ClearedAt, @ElapsedSeconds, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE EST_EQUIPMENT_ALARM SET
            CLEARED_AT = @ClearedAt, ELAPSED_SECONDS = @ElapsedSeconds,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ALARM_ID = @AlarmId";

    public async Task AddAsync(EquipmentAlarm alarm, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 INSERT(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.InsertAsync(InsertSql, AlarmRow.FromDomain(alarm), ct);
            return;
        }
        // ADR-002 활성: 알람 INSERT + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(alarm, InsertSql, isInsert: true, ct);
    }

    public async Task UpdateAsync(EquipmentAlarm alarm, CancellationToken ct = default)
    {
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, AlarmRow.FromDomain(alarm), ct);
            return;
        }
        await PersistWithOutboxAsync(alarm, UpdateSql, isInsert: false, ct);
    }

    // 알람 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 알람 행의 감사 컬럼을
    // InsertAsync/UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(EquipmentAlarm alarm, string sql, bool isInsert, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (sql, isInsert ? InsertParam(alarm, user, now) : UpdateParam(alarm, user, now)),
        };
        statements.AddRange(OutboxStatements.For(alarm.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        alarm.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters InsertParam(EquipmentAlarm alarm, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters(AlarmRow.FromDomain(alarm));
        p.Add("CreatedBy", user);
        p.Add("CreatedAt", now);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private static Dapper.DynamicParameters UpdateParam(EquipmentAlarm alarm, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("AlarmId", alarm.Id);
        p.Add("ClearedAt", alarm.ClearedAt);
        p.Add("ElapsedSeconds", alarm.ElapsedSeconds);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class AlarmRow
    {
        public string AlarmId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string AlarmCode { get; set; } = "";
        public string AlarmName { get; set; } = "";
        public string AlarmLevel { get; set; } = "";
        public DateTime OccurredAt { get; set; }
        public DateTime? ClearedAt { get; set; }
        public long? ElapsedSeconds { get; set; }
        // 읽기경로 감사 메타데이터 복원용(MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑, SELECT *).
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public EquipmentAlarm ToDomain() =>
            EquipmentAlarm.Restore(AlarmId, EquipmentId, AlarmCode, AlarmName, AlarmLevel, OccurredAt,
                ClearedAt, ElapsedSeconds, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static AlarmRow FromDomain(EquipmentAlarm a) => new()
        {
            AlarmId = a.Id,
            EquipmentId = a.EquipmentId,
            AlarmCode = a.AlarmCode,
            AlarmName = a.AlarmName,
            AlarmLevel = a.AlarmLevel,
            OccurredAt = a.OccurredAt,
            ClearedAt = a.ClearedAt,
            ElapsedSeconds = a.ElapsedSeconds
        };
    }
}
