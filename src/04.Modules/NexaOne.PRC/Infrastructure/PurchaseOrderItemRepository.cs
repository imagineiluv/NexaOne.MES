using System.Data;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.PRC.Application.PurchaseOrders;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Infrastructure;

/// <summary>Serializes Draft line edits on the parent order and maintains its quantity total.</summary>
internal sealed class PurchaseOrderItemRepository : QueryRepository, IPurchaseOrderItemStore
{
    private readonly ServiceObjectProcessor _processor;
    private readonly IBusinessMasterDirectory _masters;

    public PurchaseOrderItemRepository(EesDataSource dataSource, IBusinessMasterDirectory masters)
        : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _masters = masters ?? throw new ArgumentNullException(nameof(masters));
    }

    public Task<PurchaseItemWriteResult> TrySaveAsync(
        string purchaseOrderId, string productId, decimal quantity, string actorId, CancellationToken ct)
        => _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var guard = await LockEditableOrderAsync(connection, transaction,
                purchaseOrderId, ct);
            if (guard != PurchaseItemWriteOutcome.Applied)
                return new PurchaseItemWriteResult(guard);

            var product = await _masters.FindProductAsync(transaction, productId, ct);
            if (product?.ValidState != "Valid")
                return new PurchaseItemWriteResult(PurchaseItemWriteOutcome.InvalidProduct);

            var canonicalProductId = product.ProductId;
            var incoming = await connection.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                "SELECT INCOMING_QTY FROM PRC_PURCHASE_ITEM " +
                "WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND PRODUCT_ID=@canonicalProductId",
                new { purchaseOrderId, canonicalProductId }, transaction, cancellationToken: ct));
            if (incoming is > 0)
                return new PurchaseItemWriteResult(PurchaseItemWriteOutcome.NotEditable);

            var otherTotal = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
                "SELECT COALESCE(SUM(CAST(ORDER_QTY AS DECIMAL(38,4))),0) " +
                "FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@purchaseOrderId " +
                "AND PRODUCT_ID<>@canonicalProductId",
                new { purchaseOrderId, canonicalProductId }, transaction, cancellationToken: ct));
            if (otherTotal > PurchaseOrderItemService.MaximumQuantity - quantity)
                return new PurchaseItemWriteResult(PurchaseItemWriteOutcome.QuantityOverflow);

            var values = new
            {
                purchaseOrderId, productId = canonicalProductId, quantity, actorId,
                now = DateTime.UtcNow,
            };
            var affected = await connection.ExecuteAsync(new CommandDefinition("""
                UPDATE PRC_PURCHASE_ITEM
                   SET ORDER_QTY=@quantity, UPDATED_BY=@actorId, UPDATED_AT=@now
                 WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND PRODUCT_ID=@productId
                   AND INCOMING_QTY=0
                """, values, transaction, cancellationToken: ct));
            if (affected == 0)
            {
                await connection.ExecuteAsync(new CommandDefinition("""
                    INSERT INTO PRC_PURCHASE_ITEM
                        (PURCHASE_ORDER_ID, PRODUCT_ID, ORDER_QTY, CREATED_BY, CREATED_AT,
                         UPDATED_BY, UPDATED_AT)
                    VALUES (@purchaseOrderId, @productId, @quantity, @actorId, @now,
                            @actorId, @now)
                    """, values, transaction, cancellationToken: ct));
            }
            else if (affected != 1)
            {
                throw new DBConcurrencyException($"Purchase item update affected {affected} rows.");
            }

            var total = otherTotal + quantity;
            await UpdateHeaderTotalAsync(connection, transaction, purchaseOrderId,
                total, actorId, ct);
            return new PurchaseItemWriteResult(PurchaseItemWriteOutcome.Applied, total);
        }, IsolationLevel.Serializable, ct);

    public Task<PurchaseItemWriteResult> TryDeleteAsync(
        string purchaseOrderId, string productId, string actorId, CancellationToken ct)
        => _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var guard = await LockEditableOrderAsync(connection, transaction,
                purchaseOrderId, ct);
            if (guard != PurchaseItemWriteOutcome.Applied)
                return new PurchaseItemWriteResult(guard);

            var affected = await connection.ExecuteAsync(new CommandDefinition("""
                DELETE FROM PRC_PURCHASE_ITEM
                 WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND PRODUCT_ID=@productId
                   AND INCOMING_QTY=0
                """, new { purchaseOrderId, productId }, transaction, cancellationToken: ct));
            if (affected == 0)
            {
                var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                    "SELECT 1 FROM PRC_PURCHASE_ITEM " +
                    "WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND PRODUCT_ID=@productId",
                    new { purchaseOrderId, productId }, transaction, cancellationToken: ct));
                return new PurchaseItemWriteResult(exists.HasValue
                    ? PurchaseItemWriteOutcome.NotEditable
                    : PurchaseItemWriteOutcome.ItemNotFound);
            }
            if (affected != 1)
                throw new DBConcurrencyException($"Purchase item deletion affected {affected} rows.");

            var total = await connection.ExecuteScalarAsync<decimal>(new CommandDefinition(
                "SELECT COALESCE(SUM(CAST(ORDER_QTY AS DECIMAL(38,4))),0) " +
                "FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@purchaseOrderId",
                new { purchaseOrderId }, transaction, cancellationToken: ct));
            await UpdateHeaderTotalAsync(connection, transaction, purchaseOrderId,
                total, actorId, ct);
            return new PurchaseItemWriteResult(PurchaseItemWriteOutcome.Applied, total);
        }, IsolationLevel.Serializable, ct);

    public async Task<IReadOnlyList<PurchaseOrderItem>?> ListAsync(
        string purchaseOrderId, CancellationToken ct)
    {
        var exists = await QueryFirstOrDefaultAsync<int?>(
            "SELECT 1 FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@purchaseOrderId",
            new { purchaseOrderId }, ct);
        if (!exists.HasValue) return null;

        var rows = await QueryAsync<PurchaseItemRow>("""
            SELECT PRODUCT_ID AS ProductId, ORDER_QTY AS OrderQuantity,
                   INCOMING_QTY AS IncomingQuantity
              FROM PRC_PURCHASE_ITEM
             WHERE PURCHASE_ORDER_ID=@purchaseOrderId
             ORDER BY PRODUCT_ID
            """, new { purchaseOrderId }, ct);
        return rows.Select(static row => new PurchaseOrderItem(
            row.ProductId, row.OrderQuantity, row.IncomingQuantity)).ToArray();
    }

    private static async Task<PurchaseItemWriteOutcome> LockEditableOrderAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string purchaseOrderId, CancellationToken ct)
    {
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE PRC_PURCHASE_ORDER SET ORDER_QTY=ORDER_QTY
             WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND STATUS='Draft' AND IS_HOLD='N'
            """, new { purchaseOrderId }, transaction, cancellationToken: ct));
        if (affected == 1) return PurchaseItemWriteOutcome.Applied;
        if (affected != 0)
            throw new DBConcurrencyException($"Purchase order guard affected {affected} rows.");
        var exists = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            "SELECT 1 FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@purchaseOrderId",
            new { purchaseOrderId }, transaction, cancellationToken: ct));
        return exists.HasValue
            ? PurchaseItemWriteOutcome.NotEditable
            : PurchaseItemWriteOutcome.OrderNotFound;
    }

    private static async Task UpdateHeaderTotalAsync(
        System.Data.Common.DbConnection connection,
        System.Data.Common.DbTransaction transaction,
        string purchaseOrderId, decimal total, string actorId, CancellationToken ct)
    {
        var affected = await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE PRC_PURCHASE_ORDER
               SET ORDER_QTY=@total, UPDATED_BY=@actorId, UPDATED_AT=@now
             WHERE PURCHASE_ORDER_ID=@purchaseOrderId AND STATUS='Draft' AND IS_HOLD='N'
            """, new { purchaseOrderId, total, actorId, now = DateTime.UtcNow },
            transaction, cancellationToken: ct));
        if (affected != 1)
            throw new DBConcurrencyException($"Purchase order quantity update affected {affected} rows.");
    }

    private sealed class PurchaseItemRow
    {
        public string ProductId { get; set; } = string.Empty;
        public decimal OrderQuantity { get; set; }
        public decimal IncomingQuantity { get; set; }
    }
}
