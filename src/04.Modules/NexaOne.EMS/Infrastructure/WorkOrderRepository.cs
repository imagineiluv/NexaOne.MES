using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EMS.Infrastructure;

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
        const string sql = SelectWorkOrderSql + " WHERE WO_ID = @woId";
        var row = await QueryFirstOrDefaultAsync<WoRow>(sql, new { woId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<WorkOrder>> GetByEquipmentAsync(string equipmentId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        const string sql = SelectWorkOrderSql + @"
            WHERE EQUIPMENT_ID = @equipmentId
              AND (@from IS NULL OR ISSUED_AT >= @from)
              AND (@to IS NULL OR ISSUED_AT <= @to)";
        var rows = await QueryAsync<WoRow>(sql, new { equipmentId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<WorkOrder>().ToList();
    }

    public async Task<IReadOnlyList<WorkOrder>> GetByStatusAsync(WorkOrderStatus status, CancellationToken ct = default)
    {
        const string sql = SelectWorkOrderSql + " WHERE STATUS = @status";
        var rows = await QueryAsync<WoRow>(sql, new { status = status.ToString() }, ct);
        return rows.Select(r => r.ToDomain()).OfType<WorkOrder>().ToList();
    }

    public async Task<int> GetCountByStatusAsync(WorkOrderStatus status, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM EMS_WORK_ORDER WHERE STATUS = @status";
        return await CountAsync(sql, new { status = status.ToString() }, ct);
    }

    public async Task<bool> HasOpenLaborAsync(string woId, CancellationToken ct = default)
        => await CountAsync(
            "SELECT COUNT(*) FROM EMS_WORK_ORDER_LABOR WHERE WO_ID=@woId AND ENDED_AT IS NULL",
            new { woId }, ct) > 0;

    public async Task<MaintenanceAction?> GetActionByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT ACTION_ID, WO_ID, ACTION_TYPE, FROM_STATUS, TO_STATUS,
                                    RESULT_STATUS, ACTOR_ID, IDEMPOTENCY_KEY, ACTION_AT,
                                    SOURCE, CLIENT_CHANNEL, DEVICE_ID, CORRELATION_ID, REMARK
                             FROM EMS_MAINTENANCE_ACTION_HISTORY
                             WHERE IDEMPOTENCY_KEY = @idempotencyKey";
        var row = await QueryFirstOrDefaultAsync<ActionRow>(sql, new { idempotencyKey }, ct);
        return row?.ToDomain();
    }

    private const string SelectWorkOrderSql = @"SELECT
        WO_ID, MAINTENANCE_PLAN_ID AS PLAN_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION,
        ASSIGNEE_ID, ISSUED_AT, STARTED_AT, COMPLETED_AT, STATUS, FAILURE_CODE_ID,
        REMARK, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT
        FROM EMS_WORK_ORDER";

    public async Task AddAsync(WorkOrder wo, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO EMS_WORK_ORDER
            (WO_ID, MAINTENANCE_PLAN_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@WoId, @PlanId, @EquipmentId, @WoType, @Description, @AssigneeId, @IssuedAt, @Status,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, WoRow.FromDomain(wo), ct);
    }

    public async Task<bool> AddWithActionAsync(
        WorkOrder wo,
        MaintenanceAction action,
        CancellationToken ct = default)
    {
        const string insert = @"INSERT INTO EMS_WORK_ORDER
            (WO_ID, MAINTENANCE_PLAN_ID, EQUIPMENT_ID, WO_TYPE, DESCRIPTION, ASSIGNEE_ID, ISSUED_AT, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @WoId, @PlanId, @EquipmentId, @WoType, @Description, @AssigneeId, @IssuedAt, @Status,
                   @Actor, @Now, @Actor, @Now
            WHERE NOT EXISTS (
                SELECT 1 FROM EMS_MAINTENANCE_ACTION_HISTORY
                 WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
              AND NOT EXISTS (
                SELECT 1 FROM EMS_WORK_ORDER WHERE WO_ID = @WoId)";
        var now = DateTime.UtcNow;
        var actor = action.ActorId;
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (insert, InsertParam(wo, action.IdempotencyKey, actor, now)),
                (InsertActionSql, ActionParam(wo, action, actor, now)));
        }
        catch (DbException ex) when (IsExpectedUniqueRace(ex, allowWorkOrderIdentity: true))
        {
            return false;
        }
    }

    private const string UpdateSql = @"UPDATE EMS_WORK_ORDER SET
            STATUS = @Status, STARTED_AT = @StartedAt, COMPLETED_AT = @CompletedAt,
            FAILURE_CODE_ID = @FailureCodeId, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE WO_ID = @WoId";

    private const string GuardedUpdateSql = @"UPDATE EMS_WORK_ORDER SET
            STATUS = @Status, STARTED_AT = @StartedAt, COMPLETED_AT = @CompletedAt,
            FAILURE_CODE_ID = @FailureCodeId, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE WO_ID = @WoId AND STATUS = @FromStatus
              AND (@ActionType NOT IN ('Complete','Cancel') OR NOT EXISTS (
                  SELECT 1 FROM EMS_WORK_ORDER_LABOR
                   WHERE WO_ID=@WoId AND ENDED_AT IS NULL))";

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

    public async Task<bool> UpdateWithActionAsync(
        WorkOrder wo,
        MaintenanceAction action,
        CancellationToken ct = default)
    {
        var actor = action.ActorId;
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (GuardedUpdateSql, UpdateParam(wo, actor, now, action.FromStatus, action.ActionType)),
            (InsertActionSql, ActionParam(wo, action, actor, now)),
        };
        if (_outboxEnabled)
            statements.AddRange(OutboxStatements.For(wo.DomainEvents.OfType<IOutboxEvent>(), actor, now));

        bool updated;
        try
        {
            updated = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        }
        catch (DbException ex) when (IsExpectedUniqueRace(ex, allowWorkOrderIdentity: false))
        {
            return false;
        }
        if (updated && _outboxEnabled) wo.ClearDomainEvents();
        return updated;
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

    private static Dapper.DynamicParameters UpdateParam(
        WorkOrder wo,
        string user,
        DateTime now,
        string? fromStatus = null,
        string? actionType = null)
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
        p.Add("FromStatus", fromStatus);
        p.Add("ActionType", actionType);
        return p;
    }

    private const string InsertActionSql = @"
        INSERT INTO EMS_MAINTENANCE_ACTION_HISTORY
        (ACTION_ID, WO_ID, MAINTENANCE_PLAN_ID, EQUIPMENT_ID, MAINTENANCE_TYPE, ACTION_TYPE,
         RESULT_STATUS, ACTOR_ID, ASSIGNEE_ID, SOURCE, CLIENT_CHANNEL, DEVICE_ID,
         FAILURE_CODE_ID, REMARK, ACTION_AT, IDEMPOTENCY_KEY, FROM_STATUS, TO_STATUS,
         CORRELATION_ID, CREATED_BY, CREATED_AT)
        VALUES
        (@ActionId, @WoId, @PlanId, @EquipmentId, @MaintenanceType, @ActionType,
         @ResultStatus, @ActorId, @AssigneeId, @Source, @ClientChannel, @DeviceId,
         @FailureCodeId, @ActionRemark, @ActionAt, @IdempotencyKey, @FromStatus, @ToStatus,
         @CorrelationId, @Actor, @Now)";

    private static Dapper.DynamicParameters InsertParam(
        WorkOrder wo,
        string idempotencyKey,
        string actor,
        DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("WoId", wo.Id);
        p.Add("PlanId", wo.PlanId);
        p.Add("EquipmentId", wo.EquipmentId);
        p.Add("WoType", wo.WoType);
        p.Add("Description", wo.Description);
        p.Add("AssigneeId", wo.AssigneeId);
        p.Add("IssuedAt", wo.IssuedAt);
        p.Add("Status", wo.Status.ToString());
        p.Add("IdempotencyKey", idempotencyKey);
        p.Add("Actor", actor);
        p.Add("Now", now);
        return p;
    }

    private static Dapper.DynamicParameters ActionParam(
        WorkOrder wo,
        MaintenanceAction action,
        string actor,
        DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("ActionId", action.ActionId);
        p.Add("WoId", wo.Id);
        p.Add("PlanId", wo.PlanId);
        p.Add("EquipmentId", wo.EquipmentId);
        p.Add("MaintenanceType", wo.WoType);
        p.Add("ActionType", action.ActionType);
        p.Add("ResultStatus", wo.Status.ToString());
        p.Add("ActorId", action.ActorId);
        p.Add("AssigneeId", wo.AssigneeId);
        p.Add("Source", action.Source);
        p.Add("ClientChannel", action.ClientChannel);
        p.Add("DeviceId", action.DeviceId);
        p.Add("IdempotencyKey", action.IdempotencyKey);
        p.Add("FromStatus", action.FromStatus);
        p.Add("ToStatus", action.ToStatus);
        p.Add("CorrelationId", action.CorrelationId);
        p.Add("FailureCodeId", wo.FailureCodeId);
        p.Add("ActionRemark", action.Remark);
        p.Add("ActionAt", action.ActionAt);
        p.Add("Actor", actor);
        p.Add("Now", now);
        return p;
    }

    /// <summary>
    /// Only collapse the two races the service can resolve by re-reading its idempotency ledger.
    /// Other constraint failures (FK/CHECK/trigger/outbox/action-id) must remain visible.
    /// </summary>
    private static bool IsExpectedUniqueRace(DbException exception, bool allowWorkOrderIdentity)
    {
        var isUniqueViolation = exception switch
        {
            SqliteException sqlite =>
                sqlite.SqliteErrorCode == 19
                && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(
                    exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException",
                    StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        if (!isUniqueViolation) return false;

        var message = exception.Message;
        if (message.Contains(
                "UX_EMS_MAINTENANCE_ACTION_IDEMPOTENCY",
                StringComparison.OrdinalIgnoreCase)
            || message.Contains(
                "EMS_MAINTENANCE_ACTION_HISTORY.IDEMPOTENCY_KEY",
                StringComparison.OrdinalIgnoreCase))
            return true;

        return allowWorkOrderIdentity
               && (message.Contains("PK_EMS_WORK_ORDER", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("EMS_WORK_ORDER.WO_ID", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class ActionRow
    {
        public string ActionId { get; set; } = string.Empty;
        public string WoId { get; set; } = string.Empty;
        public string ActionType { get; set; } = string.Empty;
        public string? FromStatus { get; set; }
        public string? ToStatus { get; set; }
        public string ResultStatus { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public DateTime ActionAt { get; set; }
        public string Source { get; set; } = "Manual";
        public string ClientChannel { get; set; } = "MES";
        public string? DeviceId { get; set; }
        public string? CorrelationId { get; set; }
        public string? Remark { get; set; }

        public MaintenanceAction ToDomain() => new(
            ActionId, WoId, ActionType, FromStatus,
            string.IsNullOrWhiteSpace(ToStatus) ? ResultStatus : ToStatus,
            ActorId, IdempotencyKey, ActionAt, Source, ClientChannel,
            DeviceId, CorrelationId, Remark);
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
        // 읽기경로 감사 메타데이터(SELECT *로 채워짐, Dapper MatchNamesWithUnderscores: CREATED_BY→CreatedBy).
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public WorkOrder ToDomain()
        {
            // 영속 상태(Status/StartedAt/CompletedAt/FailureCode/Remark)를 그대로 복원한다 —
            // Create는 Status를 Issued로 강제해 상태를 유실하므로 Restore를 사용한다.
            if (!Enum.TryParse<WorkOrderStatus>(Status, out var status)) status = WorkOrderStatus.Issued;
            return WorkOrder.Restore(
                WoId, PlanId, EquipmentId, WoType, Description, AssigneeId,
                IssuedAt, StartedAt, CompletedAt, status, FailureCodeId, Remark,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);
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
