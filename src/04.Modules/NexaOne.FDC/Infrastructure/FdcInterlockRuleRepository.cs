using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcInterlockRuleRepository : QueryRepository, IFdcInterlockRuleRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcInterlockRuleRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<IReadOnlyList<FdcInterlockRule>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_INTERLOCK_RULE
            WHERE EQUIPMENT_ID = @equipmentId
            ORDER BY PARAMETER_ID, PRIORITY";
        var rows = await QueryAsync<RuleRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcInterlockRule>().ToList();
    }

    public async Task<IReadOnlyList<FdcInterlockRule>> GetActiveRulesAsync(string equipmentId, string parameterId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM FDC_INTERLOCK_RULE
            WHERE EQUIPMENT_ID = @equipmentId AND PARAMETER_ID = @parameterId AND IS_ACTIVE = 1
            ORDER BY PRIORITY";
        var rows = await QueryAsync<RuleRow>(sql, new { equipmentId, parameterId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<FdcInterlockRule>().ToList();
    }

    public async Task AddAsync(FdcInterlockRule rule, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_INTERLOCK_RULE
            (RULE_ID, RULE_NAME, EQUIPMENT_ID, PARAMETER_ID, OPERATOR, THRESHOLD_VALUE, ACTION, PRIORITY, IS_ACTIVE,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@RuleId, @RuleName, @EquipmentId, @ParameterId, @Operator, @ThresholdValue, @Action, @Priority, @IsActive,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, RuleRow.FromDomain(rule), ct);
    }

    public async Task UpdateAsync(FdcInterlockRule rule, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_INTERLOCK_RULE SET
            THRESHOLD_VALUE = @ThresholdValue, ACTION = @Action, PRIORITY = @Priority, IS_ACTIVE = @IsActive,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE RULE_ID = @RuleId";
        await _processor.UpdateAsync(sql, RuleRow.FromDomain(rule), ct);
    }

    private sealed class RuleRow
    {
        public string RuleId { get; set; } = "";
        public string RuleName { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string ParameterId { get; set; } = "";
        public string Operator { get; set; } = "";
        public decimal ThresholdValue { get; set; }
        public string Action { get; set; } = "";
        public int Priority { get; set; }
        public bool IsActive { get; set; }

        public FdcInterlockRule ToDomain() =>
            FdcInterlockRule.Restore(RuleId, RuleName, EquipmentId, ParameterId, Operator, ThresholdValue, Action, Priority, IsActive);

        public static RuleRow FromDomain(FdcInterlockRule r) => new()
        {
            RuleId = r.Id,
            RuleName = r.RuleName,
            EquipmentId = r.EquipmentId,
            ParameterId = r.ParameterId,
            Operator = r.Operator,
            ThresholdValue = r.ThresholdValue,
            Action = r.Action,
            Priority = r.Priority,
            IsActive = r.IsActive
        };
    }
}
