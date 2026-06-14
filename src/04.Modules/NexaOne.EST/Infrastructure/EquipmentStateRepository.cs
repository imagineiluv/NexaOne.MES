using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.EST.Infrastructure;

public sealed class EquipmentStateRepository : QueryRepository, IEquipmentStateRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;
    private readonly bool _outboxEnabled;

    public EquipmentStateRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
        // ADR-002 대표 슬라이스: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다.
        // (Binder 패키지 비의존 위해 GetValue 대신 인덱서 + 파싱 사용)
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<EquipmentCurrentState?> GetAsync(
        string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT EQUIPMENT_ID, PLANT_ID, CURRENT_STATE_ID, STATE_CHANGED_AT, STATE_VERSION
            FROM EST_EQUIPMENT_STATE
            WHERE EQUIPMENT_ID = @equipmentId";
        var row = await QueryFirstOrDefaultAsync<StateRow>(sql, new { equipmentId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<EquipmentCurrentState>> GetByPlantAsync(
        string plantId, CancellationToken ct = default)
    {
        const string sql = @"
            SELECT EQUIPMENT_ID, PLANT_ID, CURRENT_STATE_ID, STATE_CHANGED_AT, STATE_VERSION
            FROM EST_EQUIPMENT_STATE
            WHERE PLANT_ID = @plantId
            ORDER BY EQUIPMENT_ID";
        var rows = await QueryAsync<StateRow>(sql, new { plantId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private const string HistInsertSql = @"
            INSERT INTO EST_EQUIPMENT_STATE_HISTORY
                (HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE,
                 CHANGED_AT, CHANGED_BY, REASON, SOURCE_TYPE, TXN_HIST_KEY)
            VALUES
                (@HistId, @EquipmentId, @FromState, @ToState, @SetState,
                 @ChangedAt, @ChangedBy, @Reason, @SourceType, @TxnHistKey)";

    // 상태 업서트 SQL + 컬럼명 키 파라미터를 만든다. KEY = PK(EQUIPMENT_ID), DATA = INSERT 후보 + UPDATE SET.
    // PLANT_ID는 동일 설비의 불변 속성이라 충돌 시 같은 값으로 재대입되어도 무해 — INSERT 컬럼 보존 위해 포함.
    // BuildUpsertSql은 @<COLUMN_NAME>(대문자 SNAKE_CASE) 플레이스홀더를 쓰므로 DynamicParameters로 컬럼명 키를 맞춘다.
    private (string Sql, Dapper.DynamicParameters Param) BuildStateUpsert(EquipmentCurrentState state)
    {
        var sql = _dialect.BuildUpsertSql(
            "EST_EQUIPMENT_STATE",
            new[] { "EQUIPMENT_ID" },
            new[] { "PLANT_ID", "CURRENT_STATE_ID", "STATE_CHANGED_AT", "STATE_VERSION" });
        var r = StateRow.FromDomain(state);
        var p = new Dapper.DynamicParameters();
        p.Add("EQUIPMENT_ID", r.EquipmentId);
        p.Add("PLANT_ID", r.PlantId);
        p.Add("CURRENT_STATE_ID", r.CurrentStateId);
        p.Add("STATE_CHANGED_AT", r.StateChangedAt);
        p.Add("STATE_VERSION", r.StateVersion);
        return (sql, p);
    }

    public async Task UpsertAsync(EquipmentCurrentState state, CancellationToken ct = default)
    {
        var (sql, p) = BuildStateUpsert(state);
        // ExecuteAsync(raw): InjectAudit는 DynamicParameters의 public 프로퍼티를 반영해 컬럼 파라미터를
        // 유실시키므로 InsertAsync 대신 감사 미주입 raw 실행 경로를 쓴다(EST_EQUIPMENT_STATE는 감사 컬럼 없음).
        await _processor.ExecuteAsync(sql, p, ct);
    }

    public async Task AddHistoryAsync(EquipmentStateHistory history, CancellationToken ct = default)
    {
        await _processor.InsertAsync(HistInsertSql, HistRow.FromDomain(history), ct);
    }

    public async Task ChangeStateWithHistoryAsync(
        EquipmentCurrentState state, EquipmentStateHistory history, CancellationToken ct = default)
    {
        // 상태 업서트 + 이력 INSERT를 단일 트랜잭션으로 — 둘 다 커밋되거나 둘 다 롤백된다(부분 커밋 방지).
        // 두 문장 모두 컬럼명/PascalCase 파라미터를 그대로 쓰는 raw 실행이라 ExecuteManyAsync로 묶는다
        // (EST 두 테이블 모두 CREATED_BY 류 감사 컬럼이 없어 감사 주입이 불필요하다).
        var (upsertSql, stateParam) = BuildStateUpsert(state);
        var statements = new List<(string Sql, object? Param)>
        {
            (upsertSql, stateParam),
            (HistInsertSql, HistRow.FromDomain(history)),
        };

        // ADR-002 대표 슬라이스: outbox 활성 시 도메인 이벤트를 같은 트랜잭션에 기록한다 — 상태·이력·이벤트가
        // 함께 커밋되거나 함께 롤백돼 발행 원자성을 보장한다. 기본 비활성이면 기존 동작과 동일(미기록·적체 없음).
        if (_outboxEnabled)
        {
            var user = CurrentUserContext.UserId ?? "SYSTEM";
            statements.AddRange(OutboxStatements.For(
                state.DomainEvents.OfType<IOutboxEvent>(), user, DateTime.UtcNow));
        }

        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        state.ClearDomainEvents();
    }

    public async Task<IReadOnlyList<EquipmentStateHistory>> GetHistoryAsync(
        string equipmentId, int limit = 50, CancellationToken ct = default)
    {
        // WrapPaged가 ORDER BY와 페이징(offset 0, limit)을 붙이므로 baseSql에는 ORDER BY를 두지 않는다.
        // limit은 정수 리터럴로 임베드되어 Dapper 파라미터에서 제거한다(equipmentId만 유지).
        var baseSql = @"
            SELECT
                HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE,
                CHANGED_AT, CHANGED_BY, REASON, SOURCE_TYPE, TXN_HIST_KEY
            FROM EST_EQUIPMENT_STATE_HISTORY
            WHERE EQUIPMENT_ID = @equipmentId";
        var sql = _dialect.WrapPaged(baseSql, "CHANGED_AT DESC", 0, limit);
        var rows = await QueryAsync<HistRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    private sealed class StateRow
    {
        public string EquipmentId { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string CurrentStateId { get; set; } = "IDLE";
        public DateTime StateChangedAt { get; set; }
        public int StateVersion { get; set; } = 1;

        public EquipmentCurrentState ToDomain()
            => EquipmentCurrentState.Restore(EquipmentId, PlantId, CurrentStateId, StateChangedAt, StateVersion);

        public static StateRow FromDomain(EquipmentCurrentState s) => new()
        {
            EquipmentId    = s.Id,
            PlantId        = s.PlantId,
            CurrentStateId = s.CurrentStateId,
            StateChangedAt = s.StateChangedAt,
            StateVersion   = s.StateVersion
        };
    }

    private sealed class HistRow
    {
        public string HistId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string FromState { get; set; } = "";
        public string ToState { get; set; } = "";
        public string SetState { get; set; } = "";
        public DateTime ChangedAt { get; set; }
        public string ChangedBy { get; set; } = "";
        public string Reason { get; set; } = "";
        public string SourceType { get; set; } = "UI";
        public string? TxnHistKey { get; set; }

        public EquipmentStateHistory ToDomain()
        {
            var result = EquipmentStateHistory.Create(
                HistId, EquipmentId, FromState, ToState, SetState,
                ChangedAt, ChangedBy, Reason, SourceType, TxnHistKey);
            return result.Value;
        }

        public static HistRow FromDomain(EquipmentStateHistory h) => new()
        {
            HistId      = h.Id,
            EquipmentId = h.EquipmentId,
            FromState   = h.FromState,
            ToState     = h.ToState,
            SetState    = h.SetState,
            ChangedAt   = h.ChangedAt,
            ChangedBy   = h.ChangedBy,
            Reason      = h.Reason,
            SourceType  = h.SourceType,
            TxnHistKey  = h.TxnHistKey
        };
    }
}
