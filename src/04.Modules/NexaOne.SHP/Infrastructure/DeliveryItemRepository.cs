using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.SHP.Infrastructure;

public sealed class DeliveryItemRepository : QueryRepository, IDeliveryItemRepository
{
    private readonly ServiceObjectProcessor _processor;

    public DeliveryItemRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<DeliveryItem?> GetByIdAsync(string itemId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SHP_DELIVERY_ITEM WHERE ITEM_ID = @itemId";
        var row = await QueryFirstOrDefaultAsync<Row>(sql, new { itemId }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<DeliveryItem>> GetByOrderAsync(string orderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM SHP_DELIVERY_ITEM WHERE DELIVERY_ORDER_ID = @orderId ORDER BY CREATED_AT";
        var rows = await QueryAsync<Row>(sql, new { orderId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<DeliveryItem>().ToList();
    }

    public async Task AddAsync(DeliveryItem item, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO SHP_DELIVERY_ITEM
            (ITEM_ID, DELIVERY_ORDER_ID, PRODUCT_ID, PLANNED_QTY, ACTUAL_QTY, LOT_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@ItemId, @DeliveryOrderId, @ProductId, @PlannedQty, @ActualQty, @LotId,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, Row.FromDomain(item), ct);
    }

    public async Task UpdateAsync(DeliveryItem item, CancellationToken ct = default)
    {
        const string sql = @"UPDATE SHP_DELIVERY_ITEM SET
            ACTUAL_QTY = @ActualQty, UPDATED_BY = @UpdatedBy, UPDATED_AT = @UpdatedAt
            WHERE ITEM_ID = @ItemId";
        await _processor.UpdateAsync(sql, Row.FromDomain(item), ct);
    }

    private sealed class Row
    {
        public string   ItemId          { get; set; } = "";
        public string   DeliveryOrderId { get; set; } = "";
        public string   ProductId       { get; set; } = "";
        public decimal  PlannedQty      { get; set; }
        public decimal? ActualQty       { get; set; }
        public string?  LotId           { get; set; }
        public string   CreatedBy       { get; set; } = "";
        public DateTime  CreatedAt      { get; set; }
        public string?  UpdatedBy       { get; set; }
        public DateTime? UpdatedAt      { get; set; }

        public DeliveryItem ToDomain()
            => DeliveryItem.Restore(
                ItemId, DeliveryOrderId, ProductId, PlannedQty, ActualQty, LotId,
                CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);

        public static Row FromDomain(DeliveryItem i) => new()
        {
            ItemId = i.Id, DeliveryOrderId = i.DeliveryOrderId, ProductId = i.ProductId,
            PlannedQty = i.PlannedQty, ActualQty = i.ActualQty, LotId = i.LotId
        };
    }
}
