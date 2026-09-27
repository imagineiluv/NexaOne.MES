using System.Data;
using System.Data.Common;
using Dapper;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.PRC.Application.PurchaseOrders;

namespace NexaOne.PRC.Infrastructure;

/// <summary>Executes each status transition as one guarded row update.</summary>
internal sealed class PurchaseOrderCommandRepository(EesDataSource dataSource)
    : IPurchaseOrderCommandStore
{
    private readonly ServiceObjectProcessor _processor = new(dataSource);

    public async Task<PurchaseOrderDraftOutcome> TrySaveDraftAsync(
        PurchaseOrderHeaderDraft draft, CancellationToken ct)
    {
        try
        {
            return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                var values = new
                {
                    draft.PurchaseOrderId, draft.PlantId, draft.PurchaseOrderName,
                    draft.VendorId, draft.OrderQuantity, draft.ActorId,
                    Now = DateTime.UtcNow,
                };
                var affected = await connection.ExecuteAsync(new CommandDefinition("""
                    UPDATE PRC_PURCHASE_ORDER
                       SET PLANT_ID=@PlantId, PURCHASE_ORDER_NAME=@PurchaseOrderName,
                           VENDOR_ID=@VendorId,
                           ORDER_QTY=CASE WHEN EXISTS (
                               SELECT 1 FROM PRC_PURCHASE_ITEM
                                WHERE PURCHASE_ORDER_ID=@PurchaseOrderId
                           ) THEN (
                               SELECT SUM(CAST(ORDER_QTY AS DECIMAL(38,4)))
                                 FROM PRC_PURCHASE_ITEM
                                WHERE PURCHASE_ORDER_ID=@PurchaseOrderId
                           ) ELSE @OrderQuantity END,
                           UPDATED_BY=@ActorId, UPDATED_AT=@Now
                     WHERE PURCHASE_ORDER_ID=@PurchaseOrderId AND STATUS='Draft' AND IS_HOLD='N'
                    """, values, transaction, cancellationToken: ct));
                if (affected == 1) return PurchaseOrderDraftOutcome.Saved;
                if (affected != 0)
                    throw new DBConcurrencyException($"Purchase-order draft update affected {affected} rows.");

                var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@PurchaseOrderId",
                    values, transaction, cancellationToken: ct));
                if (exists.HasValue) return PurchaseOrderDraftOutcome.NotEditable;

                affected = await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO PRC_PURCHASE_ORDER
                        (PURCHASE_ORDER_ID, PLANT_ID, PURCHASE_ORDER_NAME, VENDOR_ID, ORDER_QTY,
                         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                    VALUES (@PurchaseOrderId, @PlantId, @PurchaseOrderName, @VendorId, @OrderQuantity,
                            @ActorId, @Now, @ActorId, @Now)
                    """, values, transaction, cancellationToken: ct));
                if (affected != 1)
                    throw new DBConcurrencyException($"Purchase-order draft insert affected {affected} rows.");
                return PurchaseOrderDraftOutcome.Saved;
            }, IsolationLevel.Serializable, ct);
        }
        catch (DbException error) when (IsUniqueViolation(error))
        {
            return PurchaseOrderDraftOutcome.IdentityConflict;
        }
    }

    public Task<PurchaseOrderCommandOutcome> TryDeleteDraftAsync(
        string purchaseOrderId, string actorId, CancellationToken ct) =>
        _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            // Lock the parent before deleting its lines. Some SQLite test stores disable FK cascades.
            var affected = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE PRC_PURCHASE_ORDER SET ORDER_QTY=ORDER_QTY
                 WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND STATUS='Draft' AND IS_HOLD='N'
                   AND NOT EXISTS (
                       SELECT 1 FROM PRC_PURCHASE_ITEM
                        WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND INCOMING_QTY>0
                   )
                """, new { purchaseOrderId }, transaction, cancellationToken: ct));
            if (affected == 0)
            {
                var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@purchaseOrderId",
                    new { purchaseOrderId }, transaction, cancellationToken: ct));
                return exists.HasValue
                    ? PurchaseOrderCommandOutcome.NotAllowed
                    : PurchaseOrderCommandOutcome.NotFound;
            }
            if (affected != 1)
                throw new DBConcurrencyException($"Purchase-order delete guard affected {affected} rows.");

            await connection.ExecuteAsync(new CommandDefinition(
                "DELETE FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@purchaseOrderId",
                new { purchaseOrderId }, transaction, cancellationToken: ct));
            affected = await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM PRC_PURCHASE_ORDER
                 WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND STATUS='Draft' AND IS_HOLD='N'
                """, new { purchaseOrderId }, transaction, cancellationToken: ct));
            if (affected != 1)
                throw new DBConcurrencyException($"Purchase-order deletion affected {affected} rows.");
            return PurchaseOrderCommandOutcome.Applied;
        }, IsolationLevel.Serializable, ct);

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

    private static bool IsUniqueViolation(DbException error) => error switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                  && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when error.GetType().FullName == "Microsoft.Data.SqlClient.SqlException"
            => error.GetType().GetProperty("Number")?.GetValue(error) is int number
               && number is 2601 or 2627,
        _ => false,
    };
}
