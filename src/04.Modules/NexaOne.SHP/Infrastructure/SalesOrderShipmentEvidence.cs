using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.ServiceContracts.Shp;

namespace NexaOne.SHP.Infrastructure;

/// <summary>SLS는 SHP 테이블을 직접 읽지 않고 소유자가 제공한 출하 스냅샷만 확인합니다.</summary>
public sealed class SalesOrderShipmentEvidence : ISalesOrderShipmentEvidence
{
    public async Task<SalesOrderShipmentSnapshot?> FindAsync(
        DbTransaction transaction, string deliveryOrderId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        if (string.IsNullOrWhiteSpace(deliveryOrderId) || deliveryOrderId.Length > 50)
            throw new ArgumentException("A valid delivery order ID is required.", nameof(deliveryOrderId));
        var connection = transaction.Connection;
        if (connection is null || connection.State != ConnectionState.Open
            || transaction.IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("A live Serializable transaction is required.");

        var order = await connection.QuerySingleOrDefaultAsync<OrderRow>(new CommandDefinition(
            "SELECT ORDER_ID AS DeliveryOrderId, PLANT_ID AS PlantId, STATUS AS Status, " +
            "SHIPPED_DATE AS ShippedDate FROM SHP_DELIVERY_ORDER WHERE ORDER_ID = @deliveryOrderId",
            new { deliveryOrderId }, transaction, cancellationToken: ct));
        if (order is null) return null;

        var items = await connection.QuerySingleAsync<ItemSummary>(new CommandDefinition(
            "SELECT COUNT(1) AS ItemCount, MIN(PRODUCT_ID) AS ProductId, " +
            "MIN(PLANNED_QTY) AS PlannedQty, MIN(ACTUAL_QTY) AS ActualQty FROM SHP_DELIVERY_ITEM " +
            "WHERE DELIVERY_ORDER_ID = @deliveryOrderId",
            new { deliveryOrderId }, transaction, cancellationToken: ct));
        var history = await connection.QuerySingleAsync<HistorySummary>(new CommandDefinition(
            "SELECT COUNT(1) AS ShipmentHistoryCount, SUM(SHIPPED_QTY) AS RecordedShippedQty " +
            "FROM SHP_SHIPMENT_HISTORY WHERE DELIVERY_ORDER_ID = @deliveryOrderId",
            new { deliveryOrderId }, transaction, cancellationToken: ct));
        return new SalesOrderShipmentSnapshot(order.DeliveryOrderId, order.PlantId, order.Status,
            order.ShippedDate, items.ProductId, items.PlannedQty, items.ActualQty, items.ItemCount,
            history.RecordedShippedQty, history.ShipmentHistoryCount);
    }

    private sealed class OrderRow
    {
        public string DeliveryOrderId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime? ShippedDate { get; set; }
    }

    private sealed class ItemSummary
    {
        public string? ProductId { get; set; }
        public decimal? PlannedQty { get; set; }
        public decimal? ActualQty { get; set; }
        public int ItemCount { get; set; }
    }

    private sealed class HistorySummary
    {
        public decimal? RecordedShippedQty { get; set; }
        public int ShipmentHistoryCount { get; set; }
    }
}
