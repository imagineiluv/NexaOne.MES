using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.FDC.Infrastructure;

public sealed class FdcInterlockHistoryRepository : QueryRepository, IFdcInterlockHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;

    public FdcInterlockHistoryRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
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

    public async Task AddAsync(FdcInterlockHistory history, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO FDC_INTERLOCK_HISTORY
            (HISTORY_ID, RULE_ID, EQUIPMENT_ID, PARAMETER_ID, TRIGGER_VALUE, ACTION, MESSAGE,
             TRIGGERED_AT, RESOLVED_AT, IS_RESOLVED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@HistoryId, @RuleId, @EquipmentId, @ParameterId, @TriggerValue, @Action, @Message,
             @TriggeredAt, @ResolvedAt, @IsResolved,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, HistRow.FromDomain(history), ct);
    }

    public async Task UpdateAsync(FdcInterlockHistory history, CancellationToken ct = default)
    {
        const string sql = @"UPDATE FDC_INTERLOCK_HISTORY SET
            RESOLVED_AT = @ResolvedAt, IS_RESOLVED = @IsResolved,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE HISTORY_ID = @HistoryId";
        await _processor.UpdateAsync(sql, HistRow.FromDomain(history), ct);
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

        public FdcInterlockHistory? ToDomain()
        {
            var result = FdcInterlockHistory.Create(
                HistoryId, RuleId, EquipmentId, ParameterId, TriggerValue, Action, Message, TriggeredAt);
            if (result.IsFailure) return null;
            var h = result.Value;
            if (IsResolved && ResolvedAt.HasValue) h.Resolve(ResolvedAt.Value);
            return h;
        }

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
