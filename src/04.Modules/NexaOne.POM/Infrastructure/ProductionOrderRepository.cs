using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;

namespace NexaOne.POM.Infrastructure;

public sealed class ProductionOrderRepository : QueryRepository, IProductionOrderRepository
{
    private readonly ServiceObjectProcessor _processor;

    public ProductionOrderRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<ProductionOrder?> GetByIdAsync(string orderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_PRODUCTION_ORDER WHERE ORDER_ID = @orderId";
        var row = await QueryFirstOrDefaultAsync<OrderRow>(sql, new { orderId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<ProductionOrder>> GetByPlanAsync(string planId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM POM_PRODUCTION_ORDER WHERE PLAN_ID = @planId ORDER BY SCHEDULED_START";
        var rows = await QueryAsync<OrderRow>(sql, new { planId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<ProductionOrder>().ToList();
    }

    public async Task AddAsync(ProductionOrder order, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO POM_PRODUCTION_ORDER
            (ORDER_ID, PLAN_ID, EQUIPMENT_ID, PRODUCT_ID, ORDER_QTY,
             SCHEDULED_START, SCHEDULED_END, STATUS,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@OrderId, @PlanId, @EquipmentId, @ProductId, @OrderQty,
             @ScheduledStart, @ScheduledEnd, @Status,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, OrderRow.FromDomain(order), ct);
    }

    public async Task UpdateAsync(ProductionOrder order, CancellationToken ct = default)
    {
        const string sql = @"UPDATE POM_PRODUCTION_ORDER SET
            STATUS = @Status, ACTUAL_QTY = @ActualQty,
            ACTUAL_START = @ActualStart, ACTUAL_END = @ActualEnd,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ORDER_ID = @OrderId";
        await _processor.UpdateAsync(sql, OrderRow.FromDomain(order), ct);
    }

    private sealed class OrderRow
    {
        public string OrderId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public decimal OrderQty { get; set; }
        public decimal? ActualQty { get; set; }
        public DateTime ScheduledStart { get; set; }
        public DateTime ScheduledEnd { get; set; }
        public DateTime? ActualStart { get; set; }
        public DateTime? ActualEnd { get; set; }
        public string Status { get; set; } = "Issued";

        public ProductionOrder? ToDomain()
        {
            var result = ProductionOrder.Create(OrderId, PlanId, EquipmentId, ProductId, OrderQty, ScheduledStart, ScheduledEnd);
            if (result.IsFailure) return null;
            var o = result.Value;
            if (Status == "InProgress" && ActualStart.HasValue) o.Start(ActualStart.Value);
            else if (Status == "Completed" && ActualQty.HasValue && ActualEnd.HasValue)
            {
                if (ActualStart.HasValue) o.Start(ActualStart.Value);
                o.Complete(ActualQty.Value, ActualEnd.Value);
            }
            else if (Status == "Cancelled") o.Cancel();
            return o;
        }

        public static OrderRow FromDomain(ProductionOrder o) => new()
        {
            OrderId = o.Id,
            PlanId = o.PlanId,
            EquipmentId = o.EquipmentId,
            ProductId = o.ProductId,
            OrderQty = o.OrderQty,
            ActualQty = o.ActualQty,
            ScheduledStart = o.ScheduledStart,
            ScheduledEnd = o.ScheduledEnd,
            ActualStart = o.ActualStart,
            ActualEnd = o.ActualEnd,
            Status = o.Status.ToString()
        };
    }
}
