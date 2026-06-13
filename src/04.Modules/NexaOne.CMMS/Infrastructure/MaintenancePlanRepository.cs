using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.CMMS.Infrastructure;

public sealed class MaintenancePlanRepository : QueryRepository, IMaintenancePlanRepository
{
    private readonly ServiceObjectProcessor _processor;

    public MaintenancePlanRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<MaintenancePlan?> GetByIdAsync(string planId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM CMMS_MAINTENANCE_PLAN WHERE PLAN_ID = @planId";
        var row = await QueryFirstOrDefaultAsync<PlanRow>(sql, new { planId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<MaintenancePlan>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM CMMS_MAINTENANCE_PLAN
            WHERE EQUIPMENT_ID = @equipmentId ORDER BY SCHEDULED_DATE";
        var rows = await QueryAsync<PlanRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<MaintenancePlan>().ToList();
    }

    public async Task<IReadOnlyList<MaintenancePlan>> GetByStatusAsync(MaintenancePlanStatus status, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM CMMS_MAINTENANCE_PLAN WHERE STATUS = @status ORDER BY SCHEDULED_DATE";
        var rows = await QueryAsync<PlanRow>(sql, new { status = status.ToString() }, ct);
        return rows.Select(r => r.ToDomain()).OfType<MaintenancePlan>().ToList();
    }

    public async Task AddAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO CMMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE,
             SCHEDULED_DATE, ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PlanId, @PlanName, @EquipmentId, @PlanType, @CycleType,
             @ScheduledDate, @EstimatedDurationHours, @AssigneeId, @Status,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, PlanRow.FromDomain(plan), ct);
    }

    public async Task UpdateAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        const string sql = @"UPDATE CMMS_MAINTENANCE_PLAN SET
            STATUS = @Status, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PLAN_ID = @PlanId";
        await _processor.UpdateAsync(sql, PlanRow.FromDomain(plan), ct);
    }

    private sealed class PlanRow
    {
        public string  PlanId                  { get; set; } = "";
        public string  PlanName                { get; set; } = "";
        public string  EquipmentId             { get; set; } = "";
        public string  PlanType                { get; set; } = "";
        public string  CycleType               { get; set; } = "";
        public DateTime ScheduledDate          { get; set; }
        public decimal EstimatedDurationHours  { get; set; }
        public string  AssigneeId              { get; set; } = "";
        public string  Status                  { get; set; } = "Planned";

        public MaintenancePlan? ToDomain()
        {
            var r = MaintenancePlan.Create(PlanId, PlanName, EquipmentId, PlanType,
                CycleType, ScheduledDate, EstimatedDurationHours, AssigneeId);
            if (r.IsFailure) return null;
            var p = r.Value;
            if (Status == "InProgress") p.Start();
            else if (Status == "Completed") { p.Start(); p.Complete(); }
            else if (Status == "Cancelled") p.Cancel();
            return p;
        }

        public static PlanRow FromDomain(MaintenancePlan p) => new()
        {
            PlanId = p.Id, PlanName = p.PlanName, EquipmentId = p.EquipmentId,
            PlanType = p.PlanType, CycleType = p.CycleType,
            ScheduledDate = p.ScheduledDate, EstimatedDurationHours = p.EstimatedDurationHours,
            AssigneeId = p.AssigneeId, Status = p.Status.ToString()
        };
    }
}
