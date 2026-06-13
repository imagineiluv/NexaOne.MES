using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcAlarmHistoryRepository : QueryRepository, IFdcAlarmHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcAlarmHistoryRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
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

    public async Task AddAsync(FdcAlarmHistory history, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_ALARM_HISTORY
            (ALARM_ID, ALARM_CONFIG_ID, EQUIPMENT_ID, PARAMETER_ID, ALARM_LEVEL, TRIGGER_VALUE, MESSAGE,
             OCCURRED_AT, CLEARED_AT, IS_CLEARED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@AlarmId, @AlarmConfigId, @EquipmentId, @ParameterId, @AlarmLevel, @TriggerValue, @Message,
             @OccurredAt, @ClearedAt, @IsCleared,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, AlarmRow.FromDomain(history), ct);
    }

    public async Task UpdateAsync(FdcAlarmHistory history, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_ALARM_HISTORY SET
            CLEARED_AT = @ClearedAt, IS_CLEARED = @IsCleared,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ALARM_ID = @AlarmId";
        await _processor.UpdateAsync(sql, AlarmRow.FromDomain(history), ct);
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

        public FdcAlarmHistory? ToDomain()
        {
            var result = FdcAlarmHistory.Create(
                AlarmId, AlarmConfigId, EquipmentId, ParameterId, AlarmLevel, TriggerValue, Message, OccurredAt);
            if (result.IsFailure) return null;
            var h = result.Value;
            if (IsCleared && ClearedAt.HasValue) h.Clear(ClearedAt.Value);
            return h;
        }

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
