using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcAlarmConfigRepository : QueryRepository, IFdcAlarmConfigRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcAlarmConfigRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<FdcAlarmConfig>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_ALARM_CONFIG
            WHERE EQUIPMENT_ID = @equipmentId
            ORDER BY PARAMETER_ID, ALARM_LEVEL";
        var rows = await QueryAsync<ConfigRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcAlarmConfig>().ToList();
    }

    public async Task<IReadOnlyList<FdcAlarmConfig>> GetActiveConfigsAsync(string equipmentId, string parameterId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_ALARM_CONFIG
            WHERE EQUIPMENT_ID = @equipmentId AND PARAMETER_ID = @parameterId AND IS_ACTIVE = 1
            ORDER BY ALARM_LEVEL";
        var rows = await QueryAsync<ConfigRow>(sql, new { equipmentId, parameterId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcAlarmConfig>().ToList();
    }

    public async Task AddAsync(FdcAlarmConfig config, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_ALARM_CONFIG
            (ALARM_CONFIG_ID, EQUIPMENT_ID, PARAMETER_ID, ALARM_LEVEL, OPERATOR, THRESHOLD_VALUE, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@AlarmConfigId, @EquipmentId, @ParameterId, @AlarmLevel, @Operator, @ThresholdValue, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, ConfigRow.FromDomain(config), ct);
    }

    public async Task UpdateAsync(FdcAlarmConfig config, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_ALARM_CONFIG SET
            ALARM_LEVEL = @AlarmLevel, OPERATOR = @Operator, THRESHOLD_VALUE = @ThresholdValue, IS_ACTIVE = @IsActive,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ALARM_CONFIG_ID = @AlarmConfigId";
        await _processor.UpdateAsync(sql, ConfigRow.FromDomain(config), ct);
    }

    private sealed class ConfigRow
    {
        public string  AlarmConfigId  { get; set; } = "";
        public string  EquipmentId    { get; set; } = "";
        public string  ParameterId    { get; set; } = "";
        public string  AlarmLevel     { get; set; } = "";
        public string  Operator       { get; set; } = "";
        public decimal ThresholdValue { get; set; }
        public bool    IsActive       { get; set; }

        public FdcAlarmConfig? ToDomain()
        {
            var result = FdcAlarmConfig.Create(AlarmConfigId, EquipmentId, ParameterId, AlarmLevel, Operator, ThresholdValue);
            if (result.IsFailure) return null;
            var c = result.Value;
            if (!IsActive) c.Deactivate();
            return c;
        }

        public static ConfigRow FromDomain(FdcAlarmConfig c) => new()
        {
            AlarmConfigId  = c.Id,
            EquipmentId    = c.EquipmentId,
            ParameterId    = c.ParameterId,
            AlarmLevel     = c.AlarmLevel,
            Operator       = c.Operator,
            ThresholdValue = c.ThresholdValue,
            IsActive       = c.IsActive
        };
    }
}
