using Microsoft.Extensions.Configuration;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Infrastructure;

public sealed class ProductionPlanRepository : QueryRepository, IProductionPlanRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly bool _outboxEnabled;

    public ProductionPlanRepository(EesDataSource dataSource, IConfiguration config) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        // ADR-002: 도메인이벤트→outbox 트랜잭션 기록은 opt-in(기본 off). 켜야 디스패처도 함께 동작한다(EST 슬라이스와 동일 게이트).
        _outboxEnabled = string.Equals(config["Events:Outbox:Enabled"], "true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<ProductionPlan?> GetByIdAsync(string planId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_PRODUCTION_PLAN WHERE PLAN_ID = @planId";
        var row = await QueryFirstOrDefaultAsync<PlanRow>(sql, new { planId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<ProductionPlan>> GetByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM POM_PRODUCTION_PLAN
            WHERE PLANT_ID = @plantId
              AND (@from IS NULL OR PLANNED_START_DATE >= @from)
              AND (@to IS NULL OR PLANNED_END_DATE <= @to)";
        var rows = await QueryAsync<PlanRow>(sql, new { plantId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<ProductionPlan>().ToList();
    }

    public async Task<int> GetCountByStatusAsync(string status, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM POM_PRODUCTION_PLAN WHERE STATUS = @status";
        return await CountAsync(sql, new { status }, ct);
    }

    public async Task AddAsync(ProductionPlan plan, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO POM_PRODUCTION_PLAN
            (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY, PLANNED_START_DATE, PLANNED_END_DATE, STATUS, REMARK,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PlanId, @PlanName, @PlantId, @ProductId, @PlannedQty, @PlannedStartDate, @PlannedEndDate, @Status, @Remark,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, PlanRow.FromDomain(plan), ct);
    }

    private const string UpdateSql = @"UPDATE POM_PRODUCTION_PLAN SET
            PLAN_NAME = @PlanName, PLANNED_QTY = @PlannedQty, STATUS = @Status, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PLAN_ID = @PlanId";

    public async Task UpdateAsync(ProductionPlan plan, CancellationToken ct = default)
    {
        // 기본(outbox off): 기존 동작 그대로 — 단건 UPDATE(감사 자동주입).
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
    private async Task PersistWithOutboxAsync(ProductionPlan plan, CancellationToken ct)
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

    private static Dapper.DynamicParameters UpdateParam(ProductionPlan plan, string user, DateTime now)
    {
        var p = new Dapper.DynamicParameters(PlanRow.FromDomain(plan));
        p.Add("UpdatedBy", user);
        p.Add("UpdatedAt", now);
        return p;
    }

    private sealed class PlanRow
    {
        public string PlanId { get; set; } = "";
        public string PlanName { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public decimal PlannedQty { get; set; }
        public DateTime PlannedStartDate { get; set; }
        public DateTime PlannedEndDate { get; set; }
        public string Status { get; set; } = "Draft";
        public string? Remark { get; set; }

        // 읽기경로 감사 메타데이터(Dapper MatchNamesWithUnderscores로 CREATED_BY→CreatedBy 등 자동 매핑; SELECT * 이므로 채워짐).
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public ProductionPlan ToDomain() =>
            ProductionPlan.Restore(PlanId, PlanName, PlantId, ProductId, PlannedQty, PlannedStartDate, PlannedEndDate,
                Enum.Parse<ProductionPlanStatus>(Status, ignoreCase: true), Remark,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static PlanRow FromDomain(ProductionPlan p) => new()
        {
            PlanId = p.Id,
            PlanName = p.PlanName,
            PlantId = p.PlantId,
            ProductId = p.ProductId,
            PlannedQty = p.PlannedQty,
            PlannedStartDate = p.PlannedStartDate,
            PlannedEndDate = p.PlannedEndDate,
            Status = p.Status.ToString(),
            Remark = p.Remark
        };
    }
}
