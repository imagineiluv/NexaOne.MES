using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.CMMS.Infrastructure;

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

    public async Task<IReadOnlyList<MaintenancePlan>> GetDueAsync(DateTime asOf, CancellationToken ct = default)
    {
        // 도래(due): 예정일이 기준시각 이하 + 아직 진행 가능한 상태(완료/취소 제외). STATUS는 enum명 문자열로 저장되므로
        // 종료 상태 이름을 그대로 NOT IN으로 배제한다(GetByStatusAsync의 status.ToString() 저장 규약과 동일). 정렬은 SCHEDULED_DATE.
        const string sql = @"SELECT * FROM CMMS_MAINTENANCE_PLAN
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

    private const string InsertSql = @"INSERT INTO CMMS_MAINTENANCE_PLAN
            (PLAN_ID, PLAN_NAME, EQUIPMENT_ID, PLAN_TYPE, CYCLE_TYPE,
             SCHEDULED_DATE, ESTIMATED_DURATION_HOURS, ASSIGNEE_ID, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PlanId, @PlanName, @EquipmentId, @PlanType, @CycleType,
             @ScheduledDate, @EstimatedDurationHours, @AssigneeId, @Status,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

    private const string UpdateSql = @"UPDATE CMMS_MAINTENANCE_PLAN SET
            STATUS = @Status, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PLAN_ID = @PlanId";

    public async Task AddAsync(MaintenancePlan plan, CancellationToken ct = default)
    {
        await _processor.InsertAsync(InsertSql, PlanRow.FromDomain(plan), ct);
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

    private static Dapper.DynamicParameters UpdateParam(MaintenancePlan plan, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("PlanId", plan.Id);
        p.Add("Status", plan.Status.ToString());
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
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

        // 읽기경로: Create+전이 재생(replay)이 아닌 Restore로 영속 상태를 직접 복원한다(전이는 도메인 이벤트를
        // 발행하므로 재생 시 읽기마다 phantom 이벤트가 발생 — ADR-002 읽기경로 안전성).
        public MaintenancePlan ToDomain() =>
            MaintenancePlan.Restore(PlanId, PlanName, EquipmentId, PlanType, CycleType,
                ScheduledDate, EstimatedDurationHours, AssigneeId,
                Enum.Parse<MaintenancePlanStatus>(Status, ignoreCase: true));

        public static PlanRow FromDomain(MaintenancePlan p) => new()
        {
            PlanId = p.Id, PlanName = p.PlanName, EquipmentId = p.EquipmentId,
            PlanType = p.PlanType, CycleType = p.CycleType,
            ScheduledDate = p.ScheduledDate, EstimatedDurationHours = p.EstimatedDurationHours,
            AssigneeId = p.AssigneeId, Status = p.Status.ToString()
        };
    }
}
