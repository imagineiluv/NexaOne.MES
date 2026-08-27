using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcInterlockHistoryRepository : QueryRepository, IFdcInterlockHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public FdcInterlockHistoryRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<FdcInterlockHistory?> GetByIdAsync(
        string effectId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_INTERLOCK_HISTORY
            WHERE HISTORY_ID = @effectId";
        var rows = await QueryAsync<HistRow>(sql, new { effectId }, ct);
        return rows.FirstOrDefault()?.ToDomain();
    }

    public async Task<IReadOnlyList<FdcInterlockHistory>> GetByEquipmentAsync(
        string equipmentId, DateTime from, DateTime to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_INTERLOCK_HISTORY
            WHERE EQUIPMENT_ID = @equipmentId
              AND TRIGGERED_AT >= @from
              AND TRIGGERED_AT <= @to
            ORDER BY TRIGGERED_AT DESC";
        var rows = await QueryAsync<HistRow>(sql, new { equipmentId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcInterlockHistory>().ToList();
    }

    public async Task<IReadOnlyList<FdcInterlockHistory>> GetUnresolvedAsync(
        string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_INTERLOCK_HISTORY
            WHERE EQUIPMENT_ID = @equipmentId AND IS_RESOLVED = 0
            ORDER BY TRIGGERED_AT DESC";
        var rows = await QueryAsync<HistRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcInterlockHistory>().ToList();
    }

    public async Task<IReadOnlyList<FdcInterlockHistory>> GetUnresolvedAsync(
        string equipmentId,
        string parameterId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_INTERLOCK_HISTORY
            WHERE EQUIPMENT_ID = @equipmentId
              AND PARAMETER_ID = @parameterId
              AND IS_RESOLVED = 0
            ORDER BY TRIGGERED_AT DESC";
        var rows = await QueryAsync<HistRow>(sql, new { equipmentId, parameterId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcInterlockHistory>().ToList();
    }

    private const string InsertSql = @"INSERT INTO FDC_INTERLOCK_HISTORY
            (HISTORY_ID, RULE_ID, EQUIPMENT_ID, PARAMETER_ID, TRIGGER_VALUE, ACTION, MESSAGE,
             TRIGGERED_AT, RESOLVED_AT, IS_RESOLVED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@HistoryId, @RuleId, @EquipmentId, @ParameterId, @TriggerValue, @Action, @Message,
             @TriggeredAt, @ResolvedAt, @IsResolved,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE FDC_INTERLOCK_HISTORY SET
            RESOLVED_AT = @ResolvedAt, IS_RESOLVED = @IsResolved,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE HISTORY_ID = @HistoryId";

    public async Task AddAsync(FdcInterlockHistory history, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 INSERT(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.InsertAsync(InsertSql, HistRow.FromDomain(history), ct);
            return;
        }
        // ADR-002 활성: 이력 INSERT + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(history, InsertSql, isInsert: true, ct);
    }

    public async Task UpdateAsync(FdcInterlockHistory history, CancellationToken ct = default)
    {
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, HistRow.FromDomain(history), ct);
            return;
        }
        await PersistWithOutboxAsync(history, UpdateSql, isInsert: false, ct);
    }

    // 이력 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 이력 행의 감사 컬럼을
    // InsertAsync/UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(FdcInterlockHistory history, string sql, bool isInsert, CancellationToken ct)
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

    private static Dapper.DynamicParameters InsertParam(FdcInterlockHistory history, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters(HistRow.FromDomain(history));
        p.Add("CreatedBy", user);
        p.Add("CreatedAt", now);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private static Dapper.DynamicParameters UpdateParam(FdcInterlockHistory history, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("HistoryId", history.Id);
        p.Add("ResolvedAt", history.ResolvedAt);
        p.Add("IsResolved", history.IsResolved);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class HistRow
    {
        public string   HistoryId    { get; set; } = "";
        public string   RuleId       { get; set; } = "";
        public string   EquipmentId  { get; set; } = "";
        public string   ParameterId  { get; set; } = "";
        public decimal  TriggerValue { get; set; }
        public string   Action       { get; set; } = "";
        public string   Message      { get; set; } = "";
        public DateTime TriggeredAt  { get; set; }
        public DateTime? ResolvedAt  { get; set; }
        public bool     IsResolved   { get; set; }

        // 영속 감사 컬럼(Dapper MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑; SELECT * 이므로 채워짐).
        public string   CreatedBy    { get; set; } = "";
        public DateTime CreatedAt    { get; set; }
        public string?  UpdatedBy    { get; set; }
        public DateTime? UpdatedAt   { get; set; }

        // 읽기경로는 Restore로 영속 상태를 직접 복원한다. Create+Resolve 재생은 읽을 때마다 PHANTOM
        // FdcInterlockResolved 이벤트를 발행하므로(ADR-002 outbox 활성 시 재발행), 전이 메서드 재생을 쓰지 않는다.
        public FdcInterlockHistory ToDomain() =>
            FdcInterlockHistory.Restore(
                HistoryId, RuleId, EquipmentId, ParameterId, TriggerValue, Action, Message,
                TriggeredAt, ResolvedAt, IsResolved,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static HistRow FromDomain(FdcInterlockHistory h) => new()
        {
            HistoryId    = h.Id,
            RuleId       = h.RuleId,
            EquipmentId  = h.EquipmentId,
            ParameterId  = h.ParameterId,
            TriggerValue = h.TriggerValue,
            Action       = h.Action,
            Message      = h.Message,
            TriggeredAt  = h.TriggeredAt,
            ResolvedAt   = h.ResolvedAt,
            IsResolved   = h.IsResolved
        };
    }
}
