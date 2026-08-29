using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EMS.Infrastructure;

public sealed class MaintenancePlanRepository : QueryRepository, IMaintenancePlanRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public MaintenancePlanRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(상태 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<MaintenancePlan?> GetByIdAsync(string planId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_MAINTENANCE_PLAN WHERE PLAN_ID = @planId";
        var row = await QueryFirstOrDefaultAsync<PlanRow>(sql, new { planId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<MaintenancePlan>> GetByEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM EMS_MAINTENANCE_PLAN
            WHERE EQUIPMENT_ID = @equipmentId ORDER BY SCHEDULED_DATE";
        var rows = await QueryAsync<PlanRow>(sql, new { equipmentId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<MaintenancePlan>().ToList();
    }

    public async Task<IReadOnlyList<MaintenancePlan>> GetByStatusAsync(MaintenancePlanStatus status, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM EMS_MAINTENANCE_PLAN WHERE STATUS = @status ORDER BY SCHEDULED_DATE";
        var rows = await QueryAsync<PlanRow>(sql, new { status = status.ToString() }, ct);
        return rows.Select(r => r.ToDomain()).OfType<MaintenancePlan>().ToList();
    }

    public async Task<IReadOnlyList<MaintenancePlan>> GetDueAsync(DateTime asOf, CancellationToken ct = default)
    {
        // 도래(due): 예정일이 기준시각 이하 + 아직 진행 가능한 상태(완료/취소 제외). STATUS는 enum명 문자열로 저장되므로
        // 종료 상태 이름을 그대로 NOT IN으로 배제한다(GetByStatusAsync의 status.ToString() 저장 규약과 동일). 정렬은 SCHEDULED_DATE.
        const string sql = @"SELECT * FROM EMS_MAINTENANCE_PLAN
            WHERE SCHEDULED_DATE <= @asOf AND STATUS NOT IN (@completed, @cancelled)
            ORDER BY SCHEDULED_DATE";
        var rows = await QueryAsync<PlanRow>(sql, new
        {
            asOf,
            completed = MaintenancePlanStatus.Completed.ToString(),
            cancelled = MaintenancePlanStatus.Cancelled.ToString()
        }, ct);
        return rows.Select(r => r.ToDomain()).OfType<MaintenancePlan>().ToList();
    }

    public async Task<MaintenancePlanAction?> GetActionByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT ACTION_ID, WO_ID AS WORK_ORDER_ID,
                                    MAINTENANCE_PLAN_ID AS PLAN_ID, ACTION_TYPE,
                                    FROM_STATUS, TO_STATUS, RESULT_STATUS, ACTOR_ID,
                                    IDEMPOTENCY_KEY, ACTION_AT, SOURCE, CLIENT_CHANNEL,
                                    DEVICE_ID, CORRELATION_ID
                             FROM EMS_MAINTENANCE_ACTION_HISTORY
                             WHERE IDEMPOTENCY_KEY=@idempotencyKey";
        var row = await QueryFirstOrDefaultAsync<PlanActionRow>(
            sql, new { idempotencyKey }, ct);
        return row?.ToDomain();
    }

    private const string InsertSql = @"INSERT INTO EMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE,
             SCHEDULED_DATE, ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PlanId, @PlanName, @EquipmentId, @PlanType, @CycleType,
             @ScheduledDate, @EstimatedDurationHours, @AssigneeId, @Status,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE EMS_MAINTENANCE_PLAN SET
            STATUS = @Status, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PLAN_ID = @PlanId";

    private const string GuardedUpdateSql = @"UPDATE EMS_MAINTENANCE_PLAN SET
            STATUS = @Status, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PLAN_ID = @PlanId AND STATUS = @FromStatus";

    private const string InsertActionSql = @"
        INSERT INTO EMS_MAINTENANCE_ACTION_HISTORY
        (ACTION_ID, WO_ID, MAINTENANCE_PLAN_ID, EQUIPMENT_ID, MAINTENANCE_TYPE, ACTION_TYPE,
         RESULT_STATUS, ACTOR_ID, ASSIGNEE_ID, SOURCE, CLIENT_CHANNEL, DEVICE_ID,
         FAILURE_CODE_ID, REMARK, ACTION_AT, IDEMPOTENCY_KEY, FROM_STATUS, TO_STATUS,
         CORRELATION_ID, CREATED_BY, CREATED_AT)
        VALUES
        (@ActionId, NULL, @PlanId, @EquipmentId, @MaintenanceType, @ActionType,
         @ResultStatus, @ActorId, @AssigneeId, @Source, @ClientChannel, @DeviceId,
         NULL, NULL, @ActionAt, @IdempotencyKey, @FromStatus, @ToStatus,
         @CorrelationId, @Actor, @Now)";

    public async Task AddAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        await _processor.InsertAsync(InsertSql, PlanRow.FromDomain(plan), ct);
    }

    public async Task<bool> AddWithActionAsync(
        MaintenancePlan plan,
        MaintenancePlanAction action,
        CancellationToken ct = default)
    {
        const string insert = @"INSERT INTO EMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE,
             SCHEDULED_DATE, ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @PlanId, @PlanName, @EquipmentId, @PlanType, @CycleType,
                   @ScheduledDate, @EstimatedDurationHours, @AssigneeId, @Status,
                   @Actor, @Now, @Actor, @Now
            WHERE NOT EXISTS (
                SELECT 1 FROM EMS_MAINTENANCE_ACTION_HISTORY
                 WHERE IDEMPOTENCY_KEY=@IdempotencyKey)
              AND NOT EXISTS (
                SELECT 1 FROM EMS_MAINTENANCE_PLAN WHERE PLAN_ID=@PlanId)";
        var actor = action.ActorId;
        var now = DateTime.UtcNow;
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (insert, InsertParam(plan, action.IdempotencyKey, actor, now)),
                (InsertActionSql, ActionParam(plan, action, actor, now)));
        }
        catch (DbException ex) when (IsExpectedUniqueRace(ex, allowPlanIdentity: true))
        {
            return false;
        }
    }

    public async Task UpdateAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입), 적체 없음.
        if (!_outboxEnabled)
        {
            await _processor.UpdateAsync(UpdateSql, PlanRow.FromDomain(plan), ct);
            return;
        }
        // ADR-002 활성: 계획 UPDATE + 도메인 이벤트(EES_OUTBOX)를 같은 트랜잭션으로 — 함께 커밋/롤백돼 발행 원자성 보장.
        await PersistWithOutboxAsync(plan, ct);
    }

    public async Task<bool> UpdateWithActionAsync(
        MaintenancePlan plan,
        MaintenancePlanAction action,
        CancellationToken ct = default)
    {
        var actor = action.ActorId;
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (GuardedUpdateSql, UpdateParam(plan, actor, now, action.FromStatus)),
            (InsertActionSql, ActionParam(plan, action, actor, now)),
        };
        if (_outboxEnabled)
            statements.AddRange(OutboxStatements.For(
                plan.DomainEvents.OfType<IOutboxEvent>(), actor, now));

        bool updated;
        try
        {
            updated = await _processor.ExecuteGuardedManyAsync(ct, statements.ToArray());
        }
        catch (DbException ex) when (IsExpectedUniqueRace(ex, allowPlanIdentity: false))
        {
            return false;
        }
        if (updated && _outboxEnabled) plan.ClearDomainEvents();
        return updated;
    }

    // 계획 행 + 발행 이벤트를 한 트랜잭션으로 기록한다. ExecuteManyAsync는 raw(감사 미주입)라 계획 행의 감사 컬럼을
    // UpdateAsync 경로와 동일한 값(현재 사용자·UTC now)으로 명시 채운다. 발행 후 이벤트를 비워 재발행을 막는다.
    // (DomainEvents가 비면 데이터 행만 기록되고 outbox INSERT는 없다 — 이벤트 없는 필드 갱신은 그대로 통과.)
    private async Task PersistWithOutboxAsync(MaintenancePlan plan, CancellationToken ct)
    {
        var user = CurrentUserContext.UserId ?? "SYSTEM";
        var now = DateTime.UtcNow;
        var statements = new List<(string Sql, object? Param)>
        {
            (UpdateSql, UpdateParam(plan, user, now)),
        };
        statements.AddRange(OutboxStatements.For(plan.DomainEvents.OfType<IOutboxEvent>(), user, now));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
        plan.ClearDomainEvents();
    }

    private static Dapper.DynamicParameters UpdateParam(
        MaintenancePlan plan,
        string user,
        DateTime now,
        string? fromStatus = null)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("PlanId", plan.Id);
        p.Add("Status", plan.Status.ToString());
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        p.Add("FromStatus", fromStatus);
        return p;
    }

    private static Dapper.DynamicParameters InsertParam(
        MaintenancePlan plan,
        string idempotencyKey,
        string actor,
        DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("PlanId", plan.Id);
        p.Add("PlanName", plan.PlanName);
        p.Add("EquipmentId", plan.EquipmentId);
        p.Add("PlanType", plan.PlanType);
        p.Add("CycleType", plan.CycleType);
        p.Add("ScheduledDate", plan.ScheduledDate);
        p.Add("EstimatedDurationHours", plan.EstimatedDurationHours);
        p.Add("AssigneeId", plan.AssigneeId);
        p.Add("Status", plan.Status.ToString());
        p.Add("IdempotencyKey", idempotencyKey);
        p.Add("Actor", actor);
        p.Add("Now", now);
        return p;
    }

    private static Dapper.DynamicParameters ActionParam(
        MaintenancePlan plan,
        MaintenancePlanAction action,
        string actor,
        DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("ActionId", action.ActionId);
        p.Add("PlanId", plan.Id);
        p.Add("EquipmentId", plan.EquipmentId);
        p.Add("MaintenanceType", plan.PlanType);
        p.Add("ActionType", action.ActionType);
        p.Add("ResultStatus", plan.Status.ToString());
        p.Add("ActorId", action.ActorId);
        p.Add("AssigneeId", plan.AssigneeId);
        p.Add("Source", action.Source);
        p.Add("ClientChannel", action.ClientChannel);
        p.Add("DeviceId", action.DeviceId);
        p.Add("IdempotencyKey", action.IdempotencyKey);
        p.Add("FromStatus", action.FromStatus);
        p.Add("ToStatus", action.ToStatus);
        p.Add("CorrelationId", action.CorrelationId);
        p.Add("ActionAt", action.ActionAt);
        p.Add("Actor", actor);
        p.Add("Now", now);
        return p;
    }

    private static bool IsExpectedUniqueRace(DbException exception, bool allowPlanIdentity)
    {
        var unique = exception switch
        {
            SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                      && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException", StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        if (!unique) return false;

        var message = exception.Message;
        if (message.Contains("UX_EMS_MAINTENANCE_ACTION_IDEMPOTENCY", StringComparison.OrdinalIgnoreCase)
            || message.Contains("EMS_MAINTENANCE_ACTION_HISTORY.IDEMPOTENCY_KEY", StringComparison.OrdinalIgnoreCase))
            return true;
        return allowPlanIdentity
               && (message.Contains("PK_EMS_MAINTENANCE_PLAN", StringComparison.OrdinalIgnoreCase)
                   || message.Contains("EMS_MAINTENANCE_PLAN.PLAN_ID", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class PlanActionRow
    {
        public string ActionId { get; set; } = string.Empty;
        public string? WorkOrderId { get; set; }
        public string? PlanId { get; set; }
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

        public MaintenancePlanAction ToDomain() => new(
            ActionId, PlanId, ActionType, FromStatus,
            string.IsNullOrWhiteSpace(ToStatus) ? ResultStatus : ToStatus,
            ActorId, IdempotencyKey, ActionAt, Source, ClientChannel,
            DeviceId, CorrelationId, WorkOrderId);
    }

    private sealed class PlanRow
    {
        public string  PlanId                  { get; set; } = "";
        public string  PlanName                { get; set; } = "";
        public string  EquipmentId             { get; set; } = "";
        public string  PlanType                { get; set; } = "";
        public string  CycleType               { get; set; } = "";
        public DateTime ScheduledDate          { get; set; }
        public object EstimatedDurationHours  { get; set; } = 0m;
        public string  AssigneeId              { get; set; } = "";
        public string  Status                  { get; set; } = "Planned";

        // 영속된 감사 메타데이터(읽기경로 복원용). Dapper MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 자동 매핑(SELECT *).
        public string   CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string?  UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        // 읽기경로: Create+전이 재생(replay)이 아닌 Restore로 영속 상태를 직접 복원한다(전이는 도메인 이벤트를
        // 발행하므로 재생 시 읽기마다 phantom 이벤트가 발생 — ADR-002 읽기경로 안전성).
        // 감사필드도 함께 복원해 읽기마다 CreatedAt=UtcNow 재생성·CreatedBy="" 리셋되는 상태손실을 막는다.
        public MaintenancePlan ToDomain() =>
            MaintenancePlan.Restore(PlanId, PlanName, EquipmentId, PlanType, CycleType,
                ScheduledDate,
                Convert.ToDecimal(EstimatedDurationHours, System.Globalization.CultureInfo.InvariantCulture),
                AssigneeId,
                Enum.Parse<MaintenancePlanStatus>(Status, ignoreCase: true),
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static PlanRow FromDomain(MaintenancePlan p) => new()
        {
            PlanId = p.Id, PlanName = p.PlanName, EquipmentId = p.EquipmentId,
            PlanType = p.PlanType, CycleType = p.CycleType,
            ScheduledDate = p.ScheduledDate, EstimatedDurationHours = p.EstimatedDurationHours,
            AssigneeId = p.AssigneeId, Status = p.Status.ToString()
        };
    }
}
