using NexaOne.DLV.Application.Dlv;
using NexaOne.DLV.Domain;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.DLV.Infrastructure;

public sealed class ShipmentHistoryRepository : QueryRepository, IShipmentHistoryRepository
{
    private readonly ServiceObjectProcessor _processor;

    public ShipmentHistoryRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<IReadOnlyList<ShipmentHistory>> GetByOrderAsync(string orderId, CancellationToken ct = default)
    {
        const string sql = "SELECT * FROM DLV_SHIPMENT_HISTORY WITH(NOLOCK) WHERE DELIVERY_ORDER_ID = @orderId ORDER BY SHIPPED_AT DESC";
        var rows = await QueryAsync<Row>(sql, new { orderId }, ct);
        return rows.Select(r => r.ToDomain()).OfType<ShipmentHistory>().ToList();
    }

    public async Task AddAsync(ShipmentHistory history, CancellationToken ct = default)
    {
        const string sql = @"INSERT INTO DLV_SHIPMENT_HISTORY
            (HISTORY_ID, DELIVERY_ORDER_ID, SHIPPED_AT, SHIPPED_QTY, SHIPPED_BY, CARRIER, TRACKING_NO,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@HistoryId, @DeliveryOrderId, @ShippedAt, @ShippedQty, @ShippedBy, @Carrier, @TrackingNo,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        await _processor.InsertAsync(sql, Row.FromDomain(history), ct);
    }

    private sealed class Row
    {
        public string   HistoryId       { get; set; } = "";
        public string   DeliveryOrderId { get; set; } = "";
        public DateTime ShippedAt       { get; set; }
        public decimal  ShippedQty      { get; set; }
        public string   ShippedBy       { get; set; } = "";
        public string?  Carrier         { get; set; }
        public string?  TrackingNo      { get; set; }

        public ShipmentHistory? ToDomain() =>
            ShipmentHistory.Create(HistoryId, DeliveryOrderId, ShippedAt, ShippedQty, ShippedBy, Carrier, TrackingNo).Value;

        public static Row FromDomain(ShipmentHistory h) => new()
        {
            HistoryId = h.Id, DeliveryOrderId = h.DeliveryOrderId,
            ShippedAt = h.ShippedAt, ShippedQty = h.ShippedQty, ShippedBy = h.ShippedBy,
            Carrier = h.Carrier, TrackingNo = h.TrackingNo
        };
    }
}
