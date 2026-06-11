using NexaOne.Infrastructure.Persistence;
using NexaOne.PPM.Application.Ppm;
using NexaOne.PPM.Domain;

namespace NexaOne.PPM.Infrastructure;

public sealed class ProductionPlanRepository : QueryRepository, IProductionPlanRepository
{
    private readonly ServiceObjectProcessor _processor;

    public ProductionPlanRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<ProductionPlan?> GetByIdAsync(string planId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM PPM_PRODUCTION_PLAN WITH(NOLOCK) WHERE PLAN_ID = @planId";
        var row = await QueryFirstOrDefaultAsync<PlanRow>(sql, new { planId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<ProductionPlan>> GetByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM PPM_PRODUCTION_PLAN WITH(NOLOCK)
            WHERE PLANT_ID = @plantId
              AND (@from IS NULL OR PLANNED_START_DATE >= @from)
              AND (@to IS NULL OR PLANNED_END_DATE <= @to)";
        var rows = await QueryAsync<PlanRow>(sql, new { plantId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<ProductionPlan>().ToList();
    }

    public async Task<int> GetCountByStatusAsync(string status, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM PPM_PRODUCTION_PLAN WITH(NOLOCK) WHERE STATUS = @status";
        return await CountAsync(sql, new { status }, ct);
    }

    public async Task AddAsync(ProductionPlan plan, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO PPM_PRODUCTION_PLAN
            (PLAN_ID, PLAN_NAME, PLANT_ID, PRODUCT_ID, PLANNED_QTY, PLANNED_START_DATE, PLANNED_END_DATE, STATUS, REMARK,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@PlanId, @PlanName, @PlantId, @ProductId, @PlannedQty, @PlannedStartDate, @PlannedEndDate, @Status, @Remark,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, PlanRow.FromDomain(plan), ct);
    }

    public async Task UpdateAsync(ProductionPlan plan, CancellationToken ct = default)
    {
        const string sql = @"UPDATE PPM_PRODUCTION_PLAN SET
            PLAN_NAME = @PlanName, PLANNED_QTY = @PlannedQty, STATUS = @Status, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE PLAN_ID = @PlanId";
        await _processor.UpdateAsync(sql, PlanRow.FromDomain(plan), ct);
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

        public ProductionPlan? ToDomain() =>
            ProductionPlan.Create(PlanId, PlanName, PlantId, ProductId, PlannedQty, PlannedStartDate, PlannedEndDate, Remark).Value;

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
