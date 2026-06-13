using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EST.Infrastructure;

public sealed class EquipmentStateRepository : QueryRepository, IEquipmentStateRepository
{
    private readonly ServiceObjectProcessor _processor;

    public EquipmentStateRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

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
        const string sql = @"
            MERGE EST_EQUIPMENT_STATE WITH(HOLDLOCK) AS tgt
            USING (VALUES (@EquipmentId, @PlantId, @CurrentStateId, @StateChangedAt, @StateVersion))
                AS src(EQUIPMENT_ID, PLANT_ID, CURRENT_STATE_ID, STATE_CHANGED_AT, STATE_VERSION)
            ON tgt.EQUIPMENT_ID = src.EQUIPMENT_ID
            WHEN MATCHED THEN
                UPDATE SET CURRENT_STATE_ID = src.CURRENT_STATE_ID,
                           STATE_CHANGED_AT = src.STATE_CHANGED_AT,
                           STATE_VERSION    = src.STATE_VERSION
            WHEN NOT MATCHED THEN
                INSERT (EQUIPMENT_ID, PLANT_ID, CURRENT_STATE_ID, STATE_CHANGED_AT, STATE_VERSION)
                VALUES (src.EQUIPMENT_ID, src.PLANT_ID, src.CURRENT_STATE_ID, src.STATE_CHANGED_AT, src.STATE_VERSION);";
        await _processor.InsertAsync(sql, StateRow.FromDomain(state), ct);
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
        const string sql = @"
            SELECT TOP (@limit)
                HIST_ID, EQUIPMENT_ID, FROM_STATE, TO_STATE, SET_STATE,
                CHANGED_AT, CHANGED_BY, REASON, SOURCE_TYPE, TXN_HIST_KEY
            FROM EST_EQUIPMENT_STATE_HISTORY
            WHERE EQUIPMENT_ID = @equipmentId
            ORDER BY CHANGED_AT DESC";
        var rows = await QueryAsync<HistRow>(sql, new { equipmentId, limit }, ct);
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
