using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Shp;
using NexaOne.SLS.Application;

namespace NexaOne.SLS.Infrastructure;

/// <summary>SLS 수주 연결을 소유하며 SHP 소유 writer를 같은 트랜잭션 안에서 호출합니다.</summary>
internal sealed class SalesOrderDeliveryRepository : ISalesOrderDeliveryStore
{
    private readonly ServiceObjectProcessor _processor;
    private readonly IBusinessMasterDirectory _masters;
    private readonly ISalesOrderShipmentIntake _shipments;
    private readonly ISalesOrderShipmentEvidence _shipmentEvidence;

    private const string SelectOrderSql = """
        SELECT STATUS AS Status, IS_HOLD AS IsHold, DELIVERY_ORDER_ID AS DeliveryOrderId,
               PLANT_ID AS PlantId, CUSTOMER_ID AS CustomerId, PRODUCT_ID AS ProductId,
               PLAN_END_DATE AS PlanEndDate, PLAN_QTY AS PlanQty,
               DELIVERED_QTY AS DeliveredQty
          FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID = @salesOrderId
        """;

    public SalesOrderDeliveryRepository(EesDataSource dataSource, IBusinessMasterDirectory masters,
        ISalesOrderShipmentIntake shipments, ISalesOrderShipmentEvidence shipmentEvidence)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _masters = masters ?? throw new ArgumentNullException(nameof(masters));
        _shipments = shipments ?? throw new ArgumentNullException(nameof(shipments));
        _shipmentEvidence = shipmentEvidence ?? throw new ArgumentNullException(nameof(shipmentEvidence));
    }

    public async Task<SalesOrderDeliveryOutcome> TryRequestAsync(
        string salesOrderId, string deliveryOrderId, string deliveryItemId, string actorId, CancellationToken ct)
    {
        try
        {
            return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                var order = await connection.QuerySingleOrDefaultAsync<SalesOrderRow>(new CommandDefinition(
                    SelectOrderSql, new { salesOrderId }, transaction, cancellationToken: ct));
                if (order is null) return SalesOrderDeliveryOutcome.SalesOrderNotFound;
                if (order.Status is not ("Draft" or "Confirmed") || order.IsHold != "N"
                    || order.DeliveryOrderId is not null || order.DeliveredQty != 0)
                    return SalesOrderDeliveryOutcome.NotRequestable;
                if (string.IsNullOrWhiteSpace(order.CustomerId) || string.IsNullOrWhiteSpace(order.ProductId)
                    || string.IsNullOrWhiteSpace(order.PlantId) || order.PlanQty <= 0
                    || order.PlanEndDate is null or { Year: 1 })
                    return SalesOrderDeliveryOutcome.InvalidReference;

                var customer = await _masters.FindCustomerAsync(transaction, order.CustomerId, ct);
                var product = await _masters.FindProductAsync(transaction, order.ProductId, ct);
                var plantId = await _masters.FindPlantAsync(transaction, order.PlantId, ct);
                if (customer?.IsActive != true || string.IsNullOrWhiteSpace(customer.CustomerName)
                    || customer.CustomerName.Length > 200 || product?.ValidState != "Valid" || plantId is null)
                    return SalesOrderDeliveryOutcome.InvalidReference;

                var updated = await connection.ExecuteAsync(new CommandDefinition(
                    """
                    UPDATE SLS_SALES_ORDER
                       SET STATUS = 'Confirmed', DELIVERY_ORDER_ID = @deliveryOrderId,
                           UPDATED_BY = @actorId, UPDATED_AT = @now
                     WHERE SALES_ORDER_ID = @salesOrderId AND STATUS IN ('Draft', 'Confirmed')
                       AND IS_HOLD = 'N' AND DELIVERY_ORDER_ID IS NULL AND DELIVERED_QTY = 0
                       AND PLAN_QTY > 0 AND PLAN_END_DATE IS NOT NULL
                    """, new { salesOrderId, deliveryOrderId, actorId, now = DateTime.UtcNow },
                    transaction, cancellationToken: ct));
                if (updated == 0) return SalesOrderDeliveryOutcome.NotRequestable;
                if (updated != 1) throw new DBConcurrencyException($"Sales order delivery link affected {updated} rows.");

                await _shipments.CreateDraftAsync(transaction, new SalesOrderShipmentDraft(
                    deliveryOrderId, deliveryItemId, customer.CustomerName, plantId,
                    product.ProductId, order.PlanEndDate.Value, order.PlanQty, actorId), ct);
                return SalesOrderDeliveryOutcome.Requested;
            }, IsolationLevel.Serializable, ct);
        }
        catch (DbException error) when (IsUniqueViolation(error))
        {
            // The transaction owner rolls back the SLS link even if the SHP item insert failed after its order insert.
            return SalesOrderDeliveryOutcome.DeliveryIdentityConflict;
        }
    }

    public Task<SalesOrderDeliveryConfirmationResult> TryConfirmAsync(
        string salesOrderId, string actorId, CancellationToken ct)
        => _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var order = await connection.QuerySingleOrDefaultAsync<SalesOrderRow>(new CommandDefinition(
                SelectOrderSql, new { salesOrderId }, transaction, cancellationToken: ct));
            if (order is null)
                return new SalesOrderDeliveryConfirmationResult(
                    SalesOrderDeliveryConfirmationOutcome.SalesOrderNotFound);
            if (order.DeliveryOrderId is null || order.PlanQty <= 0)
                return new SalesOrderDeliveryConfirmationResult(
                    SalesOrderDeliveryConfirmationOutcome.NotConfirmable);
            if (order.Status == "Delivered" && order.DeliveredQty == order.PlanQty)
                return new SalesOrderDeliveryConfirmationResult(
                    SalesOrderDeliveryConfirmationOutcome.AlreadyConfirmed,
                    order.DeliveryOrderId, order.DeliveredQty);
            if (order.Status is not ("Confirmed" or "Producing") || order.IsHold != "N"
                || order.DeliveredQty != 0 || string.IsNullOrWhiteSpace(order.PlantId)
                || string.IsNullOrWhiteSpace(order.ProductId))
                return new SalesOrderDeliveryConfirmationResult(
                    SalesOrderDeliveryConfirmationOutcome.NotConfirmable);

            var evidence = await _shipmentEvidence.FindAsync(transaction, order.DeliveryOrderId, ct);
            if (evidence is null || evidence.Status != "Shipped" || evidence.ShippedDate is null)
                return new SalesOrderDeliveryConfirmationResult(
                    SalesOrderDeliveryConfirmationOutcome.ShipmentNotShipped);
            // SHP의 Shipped는 전량 출하 상태다. 상세 실적이 있으면 그 수량도 수주 계획과 일치해야 한다.
            if (evidence.ItemCount != 1 || evidence.PlannedQty != order.PlanQty
                || (evidence.ActualQty is { } actualQty && actualQty != order.PlanQty)
                || (evidence.ShipmentHistoryCount > 0 && evidence.RecordedShippedQty != order.PlanQty)
                || !string.Equals(evidence.PlantId, order.PlantId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(evidence.ProductId, order.ProductId, StringComparison.OrdinalIgnoreCase))
                return new SalesOrderDeliveryConfirmationResult(
                    SalesOrderDeliveryConfirmationOutcome.ShipmentMismatch);

            var updated = await connection.ExecuteAsync(new CommandDefinition(
                """
                UPDATE SLS_SALES_ORDER
                   SET STATUS = 'Delivered', DELIVERED_QTY = PLAN_QTY,
                       UPDATED_BY = @actorId, UPDATED_AT = @now
                 WHERE SALES_ORDER_ID = @salesOrderId AND DELIVERY_ORDER_ID = @deliveryOrderId
                   AND STATUS IN ('Confirmed', 'Producing') AND IS_HOLD = 'N'
                   AND DELIVERED_QTY = 0 AND PLAN_QTY = @planQty
                """, new
                {
                    salesOrderId, deliveryOrderId = order.DeliveryOrderId,
                    actorId, planQty = order.PlanQty, now = DateTime.UtcNow,
                }, transaction, cancellationToken: ct));
            if (updated == 0)
                return new SalesOrderDeliveryConfirmationResult(
                    SalesOrderDeliveryConfirmationOutcome.NotConfirmable);
            if (updated != 1)
                throw new DBConcurrencyException($"Sales order delivery confirmation affected {updated} rows.");
            return new SalesOrderDeliveryConfirmationResult(
                SalesOrderDeliveryConfirmationOutcome.Confirmed,
                order.DeliveryOrderId, order.PlanQty);
        }, IsolationLevel.Serializable, ct);

    private static bool IsUniqueViolation(DbException error) => error switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                  && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when error.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
            => error.GetType().GetProperty("Number")?.GetValue(error) is int number
               && number is 2601 or 2627,
        _ => false,
    };

    private sealed class SalesOrderRow
    {
        public string Status { get; set; } = string.Empty;
        public string IsHold { get; set; } = string.Empty;
        public string? DeliveryOrderId { get; set; }
        public string? PlantId { get; set; }
        public string? CustomerId { get; set; }
        public string? ProductId { get; set; }
        public DateTime? PlanEndDate { get; set; }
        public decimal PlanQty { get; set; }
        public decimal DeliveredQty { get; set; }
    }
}
