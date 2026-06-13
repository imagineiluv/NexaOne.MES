using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.CMMS.Infrastructure;

public sealed class WorkOrderRepository : QueryRepository, IWorkOrderRepository
{
    private readonly ServiceObjectProcessor _processor;

    public WorkOrderRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<WorkOrder?> GetByIdAsync(string woId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM CMMS_WORK_ORDER WHERE WO_ID = @woId";
        var row = await QueryFirstOrDefaultAsync<WoRow>(sql, new { woId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<WorkOrder>> GetByEquipmentAsync(string equipmentId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM CMMS_WORK_ORDER
            WHERE EQUIPMENT_ID = @equipmentId
              AND (@from IS NULL OR ISSUED_AT >= @from)
              AND (@to IS NULL OR ISSUED_AT <= @to)";
        var rows = await QueryAsync<WoRow>(sql, new { equipmentId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<WorkOrder>().ToList();
    }

    public async Task<IReadOnlyList<WorkOrder>> GetByStatusAsync(WorkOrderStatus status, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM CMMS_WORK_ORDER WHERE STATUS = @status";
        var rows = await QueryAsync<WoRow>(sql, new { status = status.ToString() }, ct);
        return rows.Select(r => r.ToDomain()).OfType<WorkOrder>().ToList();
    }

    public async Task AddAsync(WorkOrder wo, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO CMMS_WORK_ORDER
            (WO_ID, PLAN_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@WoId, @PlanId, @EquipmentId, @WoType, @Description, @AssigneeId, @IssuedAt, @Status,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, WoRow.FromDomain(wo), ct);
    }

    public async Task UpdateAsync(WorkOrder wo, CancellationToken ct = default)
    {
        const string sql = @"UPDATE CMMS_WORK_ORDER SET
            STATUS = @Status, STARTED_AT = @StartedAt, COMPLETED_AT = @CompletedAt,
            FAILURE_CODE_ID = @FailureCodeId, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE WO_ID = @WoId";
        await _processor.UpdateAsync(sql, WoRow.FromDomain(wo), ct);
    }

    private sealed class WoRow
    {
        public string WoId { get; set; } = "";
        public string? PlanId { get; set; }
        public string EquipmentId { get; set; } = "";
        public string WoType { get; set; } = "";
        public string Description { get; set; } = "";
        public string AssigneeId { get; set; } = "";
        public DateTime IssuedAt { get; set; }
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string Status { get; set; } = "Issued";
        public string? FailureCodeId { get; set; }
        public string? Remark { get; set; }

        public WorkOrder? ToDomain()
        {
            if (!Enum.TryParse<WorkOrderStatus>(Status, out var status)) status = WorkOrderStatus.Issued;
            return WorkOrder.Create(WoId, EquipmentId, WoType, Description, AssigneeId, IssuedAt, PlanId).Value;
        }

        public static WoRow FromDomain(WorkOrder w) => new()
        {
            WoId = w.Id,
            PlanId = w.PlanId,
            EquipmentId = w.EquipmentId,
            WoType = w.WoType,
            Description = w.Description,
            AssigneeId = w.AssigneeId,
            IssuedAt = w.IssuedAt,
            StartedAt = w.StartedAt,
            CompletedAt = w.CompletedAt,
            Status = w.Status.ToString(),
            FailureCodeId = w.FailureCodeId,
            Remark = w.Remark
        };
    }
}
