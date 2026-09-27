using System.Data;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.PRC.Application.PurchaseOrders;

namespace NexaOne.PRC.Infrastructure;

/// <summary>Performs each hold transition as one guarded row update.</summary>
internal sealed class PurchaseOrderHoldRepository(EesDataSource dataSource)
    : IPurchaseOrderHoldStore
{
    private readonly ServiceObjectProcessor _processor = new(dataSource);

    public Task<PurchaseOrderHoldOutcome> TryHoldAsync(
        string purchaseOrderId, string actorId, CancellationToken ct) =>
        ChangeAsync(purchaseOrderId, actorId, hold: true, ct);

    public Task<PurchaseOrderHoldOutcome> TryReleaseAsync(
        string purchaseOrderId, string actorId, CancellationToken ct) =>
        ChangeAsync(purchaseOrderId, actorId, hold: false, ct);

    private Task<PurchaseOrderHoldOutcome> ChangeAsync(
        string purchaseOrderId, string actorId, bool hold, CancellationToken ct) =>
        _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            // The finite status guards fail closed if PRC later adds a new state.
            var sql = hold ? """
                UPDATE PRC_PURCHASE_ORDER
                   SET IS_HOLD='Y', UPDATED_BY=@actorId, UPDATED_AT=@now
                 WHERE PURCHASE_ORDER_ID=@purchaseOrderId
                   AND STATUS IN ('Draft','Ordered') AND IS_HOLD='N'
                """ : """
                UPDATE PRC_PURCHASE_ORDER
                   SET IS_HOLD='N', UPDATED_BY=@actorId, UPDATED_AT=@now
                 WHERE PURCHASE_ORDER_ID=@purchaseOrderId
                   AND STATUS IN ('Draft','Ordered','Incoming','Closed') AND IS_HOLD='Y'
                """;
            var affected = await connection.ExecuteAsync(new CommandDefinition(
                sql, new { purchaseOrderId, actorId, now = DateTime.UtcNow },
                transaction, cancellationToken: ct));
            if (affected == 1) return PurchaseOrderHoldOutcome.Applied;
            if (affected != 0)
                throw new DBConcurrencyException($"Purchase-order hold update affected {affected} rows.");

            var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                "SELECT 1 FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@purchaseOrderId",
                new { purchaseOrderId }, transaction, cancellationToken: ct));
            return exists.HasValue
                ? PurchaseOrderHoldOutcome.NotAllowed
                : PurchaseOrderHoldOutcome.NotFound;
        }, IsolationLevel.Serializable, ct);
}
