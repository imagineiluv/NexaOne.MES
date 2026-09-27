using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.ServiceContracts.Shp;

namespace NexaOne.SHP.Infrastructure;

/// <summary>SHP 물리 테이블은 SHP만 기록하며, SLS의 수주 전이와 커밋 경계를 공유합니다.</summary>
public sealed class SalesOrderShipmentIntake : ISalesOrderShipmentIntake
{
    private const decimal MaximumQuantity = 99999999999999.9999m; // DECIMAL(18,4)

    public async Task CreateDraftAsync(
        DbTransaction transaction, SalesOrderShipmentDraft draft, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(draft);
        var connection = transaction.Connection;
        if (connection is null || connection.State != ConnectionState.Open
            || transaction.IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("A live Serializable transaction is required.");
        if (!ValidId(draft.DeliveryOrderId) || !ValidId(draft.DeliveryItemId)
            || !ValidId(draft.PlantId) || !ValidId(draft.ProductId) || !ValidId(draft.ActorId)
            || string.IsNullOrWhiteSpace(draft.CustomerName) || draft.CustomerName.Length > 200
            || draft.RequestedDate == default || draft.PlannedQty <= 0
            || draft.PlannedQty > MaximumQuantity || decimal.Round(draft.PlannedQty, 4) != draft.PlannedQty)
            throw new ArgumentException("The shipment draft is outside SHP storage boundaries.", nameof(draft));

        var now = DateTime.UtcNow;
        var orderRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO SHP_DELIVERY_ORDER
                (ORDER_ID, CUSTOMER_NAME, PLANT_ID, REQUESTED_DATE, STATUS,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@DeliveryOrderId, @CustomerName, @PlantId, @RequestedDate, 'Draft',
                    @ActorId, @now, @ActorId, @now)
            """, new
            {
                draft.DeliveryOrderId, draft.CustomerName, draft.PlantId,
                draft.RequestedDate, draft.ActorId, now,
            }, transaction, cancellationToken: ct));
        if (orderRows != 1) throw new DBConcurrencyException($"Shipment order insert affected {orderRows} rows.");

        var itemRows = await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO SHP_DELIVERY_ITEM
                (ITEM_ID, DELIVERY_ORDER_ID, PRODUCT_ID, PLANNED_QTY,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@DeliveryItemId, @DeliveryOrderId, @ProductId, @PlannedQty,
                    @ActorId, @now, @ActorId, @now)
            """, new
            {
                draft.DeliveryItemId, draft.DeliveryOrderId, draft.ProductId,
                draft.PlannedQty, draft.ActorId, now,
            }, transaction, cancellationToken: ct));
        if (itemRows != 1) throw new DBConcurrencyException($"Shipment item insert affected {itemRows} rows.");
    }

    private static bool ValidId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value == value.Trim() && value.Length <= 50;
}
