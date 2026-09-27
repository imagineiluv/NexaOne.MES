using System.Data;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.PRC.Application.PurchaseOrders;

namespace NexaOne.PRC.Infrastructure;

/// <summary>Executes each status transition as one guarded row update.</summary>
internal sealed class PurchaseOrderCommandRepository(EesDataSource dataSource)
    : IPurchaseOrderCommandStore
{
    private readonly ServiceObjectProcessor _processor = new(dataSource);

    public Task<PurchaseOrderCommandOutcome> TryOrderAsync(
        string purchaseOrderId, string actorId, CancellationToken ct) => ChangeAsync("""
            UPDATE PRC_PURCHASE_ORDER
               SET STATUS='Ordered', UPDATED_BY=@actorId, UPDATED_AT=@now
             WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND STATUS='Draft' AND IS_HOLD='N'
               AND EXISTS (SELECT 1 FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@purchaseOrderId)
            """, purchaseOrderId, actorId, ct);

    public Task<PurchaseOrderCommandOutcome> TryCloseAsync(
        string purchaseOrderId, string actorId, CancellationToken ct) => ChangeAsync("""
            UPDATE PRC_PURCHASE_ORDER
               SET STATUS='Closed', UPDATED_BY=@actorId, UPDATED_AT=@now
             WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND STATUS='Incoming' AND IS_HOLD='N'
               AND EXISTS (SELECT 1 FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@purchaseOrderId)
               AND NOT EXISTS (
                   SELECT 1 FROM PRC_PURCHASE_ITEM
                    WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND INCOMING_QTY < ORDER_QTY
               )
            """, purchaseOrderId, actorId, ct);

    private Task<PurchaseOrderCommandOutcome> ChangeAsync(
        string sql, string purchaseOrderId, string actorId, CancellationToken ct) =>
        _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                sql, new { purchaseOrderId, actorId, now = DateTime.UtcNow },
                transaction, cancellationToken: ct));
            if (affected == 1) return PurchaseOrderCommandOutcome.Applied;
            if (affected != 0)
                throw new DBConcurrencyException($"Purchase-order status update affected {affected} rows.");

            var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@purchaseOrderId",
                new { purchaseOrderId }, transaction, cancellationToken: ct));
            return exists.HasValue
                ? PurchaseOrderCommandOutcome.NotAllowed
                : PurchaseOrderCommandOutcome.NotFound;
        }, IsolationLevel.Serializable, ct);
}
