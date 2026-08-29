using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Infrastructure.Persistence;
using NexaDB.Data.Abstractions.Interfaces;

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

    private const string InitializeSql = @"
        INSERT INTO EST_EQUIPMENT_STATE
            (EQUIPMENT_ID, PLANT_ID, CURRENT_STATE_ID, STATE_CHANGED_AT, STATE_VERSION)
        SELECT @EquipmentId, @PlantId, @CurrentStateId, @StateChangedAt, @StateVersion
        WHERE NOT EXISTS (
            SELECT 1 FROM EST_EQUIPMENT_STATE WHERE EQUIPMENT_ID = @EquipmentId
        )";

    public async Task<bool> TryInitializeAsync(
        EquipmentCurrentState state, CancellationToken ct = default)
    {
        var row = StateRow.FromDomain(state);
        return await _processor.ExecuteGuardedManyAsync(ct, (InitializeSql, row));
    }

    private const string HistInsertSql = @"
            INSERT INTO EST_EQUIPMENT_STATE_HISTORY
                (HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE,
                 CHANGED_AT, CHANGED_BY, REASON, SOURCE_TYPE, TXN_HIST_KEY)
            VALUES
                (@HistId, @EquipmentId, @FromState, @ToState, @SetState,
                 @ChangedAt, @ChangedBy, @Reason, @SourceType, @TxnHistKey)";

    private const string ChangeStateCasSql = @"
        UPDATE EST_EQUIPMENT_STATE SET
            CURRENT_STATE_ID = @CurrentStateId,
            STATE_CHANGED_AT = @StateChangedAt,
            STATE_VERSION = @StateVersion
        WHERE EQUIPMENT_ID = @EquipmentId
          AND PLANT_ID = @PlantId
          AND STATE_VERSION = @ExpectedVersion";

    public async Task<bool> TryChangeStateWithHistoryAsync(
        EquipmentCurrentState state,
        EquipmentStateHistory history,
        int expectedVersion,
        CancellationToken ct = default)
    {
        var stateRow = StateRow.FromDomain(state);
        var stateParam = new
        {
            stateRow.EquipmentId,
            stateRow.PlantId,
            stateRow.CurrentStateId,
            stateRow.StateChangedAt,
            stateRow.StateVersion,
            ExpectedVersion = expectedVersion,
        };
        var statements = new List<(string Sql, object? Param)>
        {
            (ChangeStateCasSql, stateParam),
            (HistInsertSql, HistRow.FromDomain(history)),
        };
        if (_outboxEnabled)
        {
            var user = CurrentUserContext.UserId ?? "SYSTEM";
            statements.AddRange(OutboxStatements.For(
                state.DomainEvents.OfType<IOutboxEvent>(), user, DateTime.UtcNow));
        }

        var changed = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        if (changed) state.ClearDomainEvents();
        return changed;
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
