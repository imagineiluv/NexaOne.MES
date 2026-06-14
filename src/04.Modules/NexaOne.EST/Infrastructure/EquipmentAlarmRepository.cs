using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EST.Infrastructure;

public sealed class EquipmentAlarmRepository : QueryRepository, IEquipmentAlarmRepository
{
    private readonly ServiceObjectProcessor _processor;

    public EquipmentAlarmRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
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

    public async Task AddAsync(EquipmentAlarm alarm, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO EST_EQUIPMENT_ALARM
            (ALARM_ID, EQUIPMENT_ID, ALARM_CODE, ALARM_NAME, ALARM_LEVEL, OCCURRED_AT,
             CLEARED_AT, ELAPSED_SECONDS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@AlarmId, @EquipmentId, @AlarmCode, @AlarmName, @AlarmLevel, @OccurredAt,
             @ClearedAt, @ElapsedSeconds, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, AlarmRow.FromDomain(alarm), ct);
    }

    public async Task UpdateAsync(EquipmentAlarm alarm, CancellationToken ct = default)
    {
        const string sql = @"UPDATE EST_EQUIPMENT_ALARM SET
            CLEARED_AT = @ClearedAt, ELAPSED_SECONDS = @ElapsedSeconds,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ALARM_ID = @AlarmId";
        await _processor.UpdateAsync(sql, AlarmRow.FromDomain(alarm), ct);
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

        public EquipmentAlarm ToDomain() =>
            EquipmentAlarm.Restore(AlarmId, EquipmentId, AlarmCode, AlarmName, AlarmLevel, OccurredAt,
                ClearedAt, ElapsedSeconds);

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
