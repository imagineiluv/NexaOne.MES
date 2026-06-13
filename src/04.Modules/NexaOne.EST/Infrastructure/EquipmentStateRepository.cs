using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Abstractions.Interfaces;

namespace NexaOne.EST.Infrastructure;

public sealed class EquipmentStateRepository : QueryRepository, IEquipmentStateRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public EquipmentStateRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
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

    public async Task UpsertAsync(EquipmentCurrentState state, CancellationToken ct = default)
    {
        // KEY_COL = ON 조건(PK): EQUIPMENT_ID. DATA_COL = INSERT 후보 + UPDATE SET 대상.
        // 원본 MERGE는 PLANT_ID를 UPDATE SET에서 제외했으나, BuildUpsertSql은 key 외 컬럼을 모두
        // INSERT+UPDATE 대상으로 다룬다. PLANT_ID는 동일 설비의 불변 속성이라 충돌 시 같은 값으로
        // 재대입되어도 무해하므로 INSERT 컬럼 보존을 위해 dataColumns에 포함한다.
        var sql = _dialect.BuildUpsertSql(
            "EST_EQUIPMENT_STATE",
            new[] { "EQUIPMENT_ID" },
            new[] { "PLANT_ID", "CURRENT_STATE_ID", "STATE_CHANGED_AT", "STATE_VERSION" });

        // BuildUpsertSql은 @<COLUMN_NAME>(대문자 SNAKE_CASE) 플레이스홀더를 쓴다 — Row의 PascalCase
        // 속성과 정합되도록 DynamicParameters로 컬럼명 키를 직접 매핑한다.
        var r = StateRow.FromDomain(state);
        var p = new Dapper.DynamicParameters();
        p.Add("EQUIPMENT_ID", r.EquipmentId);
        p.Add("PLANT_ID", r.PlantId);
        p.Add("CURRENT_STATE_ID", r.CurrentStateId);
        p.Add("STATE_CHANGED_AT", r.StateChangedAt);
        p.Add("STATE_VERSION", r.StateVersion);
        // ExecuteAsync(raw): InjectAudit는 DynamicParameters의 public 프로퍼티를 반영해 컬럼 파라미터를
        // 유실시키므로 InsertAsync 대신 감사 미주입 raw 실행 경로를 쓴다(EST_EQUIPMENT_STATE는 감사 컬럼 없음).
        await _processor.ExecuteAsync(sql, p, ct);
    }

    public async Task AddHistoryAsync(EquipmentStateHistory history, CancellationToken ct = default)
    {
        const string sql = @"
            INSERT INTO EST_EQUIPMENT_STATE_HISTORY
                (HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE,
                 CHANGED_AT, CHANGED_BY, REASON, SOURCE_TYPE, TXN_HIST_KEY)
            VALUES
                (@HistId, @EquipmentId, @FromState, @ToState, @SetState,
                 @ChangedAt, @ChangedBy, @Reason, @SourceType, @TxnHistKey)";
        await _processor.InsertAsync(sql, HistRow.FromDomain(history), ct);
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
