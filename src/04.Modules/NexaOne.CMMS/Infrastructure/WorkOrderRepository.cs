using Microsoft.Extensions.Configuration;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.CMMS.Infrastructure;

public sealed class WorkOrderRepository : QueryRepository, IWorkOrderRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public WorkOrderRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(EST 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
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

    private const string UpdateSql = @"UPDATE CMMS_WORK_ORDER SET
            STATUS = @Status, STARTED_AT = @StartedAt, COMPLETED_AT = @CompletedAt,
            FAILURE_CODE_ID = @FailureCodeId, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE WO_ID = @WoId";

    public async Task UpdateAsync(WorkOrder wo, CancellationToken ct = default)
    {
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, WoRow.FromDomain(wo), ct);
            return;
        }
        // ADR-002 활성: 작업지시 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(wo, ct);
    }

    // 작업지시 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 작업지시 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    private async Task PersistWithOutboxAsync(WorkOrder wo, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(wo, user, now)),
        };
        statements.AddRange(OutboxStatements.For(wo.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        wo.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(WorkOrder wo, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("WoId", wo.Id);
        p.Add("Status", wo.Status.ToString());
        p.Add("StartedAt", wo.StartedAt);
        p.Add("CompletedAt", wo.CompletedAt);
        p.Add("FailureCodeId", wo.FailureCodeId);
        p.Add("Remark", wo.Remark);
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
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

        public WorkOrder ToDomain()
        {
            // 영속 상태(Status/StartedAt/CompletedAt/FailureCode/Remark)를 그대로 복원한다 —
            // Create는 Status를 Issued로 강제해 상태를 유실하므로 Restore를 사용한다.
            if (!Enum.TryParse<WorkOrderStatus>(Status, out var status)) status = WorkOrderStatus.Issued;
            return WorkOrder.Restore(
                WoId, PlanId, EquipmentId, WoType, Description, AssigneeId,
                IssuedAt, StartedAt, CompletedAt, status, FailureCodeId, Remark);
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
