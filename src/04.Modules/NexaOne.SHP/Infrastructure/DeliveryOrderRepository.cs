using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.SHP.Infrastructure;

public sealed class DeliveryOrderRepository : QueryRepository, IDeliveryOrderRepository
{
    private readonly ServiceObjectProcessor _processor;

    public DeliveryOrderRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
    }

    public async Task<DeliveryOrder?> GetByIdAsync(string orderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SHP_DELIVERY_ORDER WHERE ORDER_ID = @orderId";
        var row = await QueryFirstOrDefaultAsync<OrderRow>(sql, new { orderId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<DeliveryOrder>> GetByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        const string sql = @"SELECT * FROM SHP_DELIVERY_ORDER
            WHERE PLANT_ID = @plantId
              AND (@from IS NULL OR REQUESTED_DATE >= @from)
              AND (@to IS NULL OR REQUESTED_DATE <= @to)";
        var rows = await QueryAsync<OrderRow>(sql, new { plantId, from, to }, ct);
        return rows.Select(r => r.ToDomain()).OfType<DeliveryOrder>().ToList();
    }

    public async Task<int> GetCountByStatusAsync(string status, CancellationToken ct = default)
    {
        const string sql = "SELECT COUNT(*) FROM SHP_DELIVERY_ORDER WHERE STATUS = @status";
        return await CountAsync(sql, new { status }, ct);
    }

    public async Task AddAsync(DeliveryOrder order, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SHP_DELIVERY_ORDER
            (ORDER_ID, CUSTOMER_NAME, PLANT_ID, REQUESTED_DATE, STATUS, REMARK,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@OrderId, @CustomerName, @PlantId, @RequestedDate, @Status, @Remark,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, OrderRow.FromDomain(order), ct);
    }

    public async Task UpdateAsync(DeliveryOrder order, CancellationToken ct = default)
    {
        const string sql = @"UPDATE SHP_DELIVERY_ORDER SET
            STATUS = @Status, SHIPPED_DATE = @ShippedDate, REMARK = @Remark,
            UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ORDER_ID = @OrderId";
        await _processor.UpdateAsync(sql, OrderRow.FromDomain(order), ct);
    }

    private sealed class OrderRow
    {
        public string OrderId { get; set; } = "";
        public string CustomerName { get; set; } = "";
        public string PlantId { get; set; } = "";
        public DateTime RequestedDate { get; set; }
        public DateTime? ShippedDate { get; set; }
        public string Status { get; set; } = "Draft";
        public string? Remark { get; set; }

        public DeliveryOrder ToDomain() =>
            DeliveryOrder.Restore(OrderId, CustomerName, PlantId, RequestedDate,
                Enum.Parse<DeliveryOrderStatus>(Status, ignoreCase: true), ShippedDate, Remark);

        public static OrderRow FromDomain(DeliveryOrder o) => new()
        {
            OrderId = o.Id,
            CustomerName = o.CustomerName,
            PlantId = o.PlantId,
            RequestedDate = o.RequestedDate,
            ShippedDate = o.ShippedDate,
            Status = o.Status.ToString(),
            Remark = o.Remark
        };
    }
}
