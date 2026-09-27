using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.PRC.Infrastructure;

/// <summary>Incremental SQLite upgrades skip migration DML, so backfill legacy MRP lines once.</summary>
public sealed class PurchaseItemSqliteSchemaContribution : ISqliteSchemaContribution
{
    private const string Marker = "PRC.PurchaseItems.V222";

    public string Id => Marker;

    public void Apply(DbConnection connection, DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);

        connection.Execute("""
            CREATE TABLE IF NOT EXISTS SYS_SQLITE_RECONCILIATION (
                RECONCILIATION_ID TEXT NOT NULL PRIMARY KEY,
                APPLIED_AT TEXT NOT NULL);
            """, transaction: transaction);
        if (connection.ExecuteScalar<int>(
                "SELECT COUNT(*) FROM SYS_SQLITE_RECONCILIATION WHERE RECONCILIATION_ID=@Marker",
                new { Marker }, transaction) != 0)
            return;

        connection.Execute("""
            INSERT INTO PRC_PURCHASE_ITEM
                (PURCHASE_ORDER_ID, PRODUCT_ID, ORDER_QTY, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT purchase.PURCHASE_ORDER_ID, purchase.PRODUCT_ID, purchase.ORDER_QTY,
                   purchase.CREATED_BY, purchase.CREATED_AT, purchase.UPDATED_BY, purchase.UPDATED_AT
              FROM PRC_PURCHASE_ORDER purchase
             WHERE purchase.PRODUCT_ID IS NOT NULL
               AND TRIM(purchase.PRODUCT_ID) <> ''
               AND purchase.ORDER_QTY > 0
               AND NOT EXISTS (
                   SELECT 1 FROM PRC_PURCHASE_ITEM item
                    WHERE item.PURCHASE_ORDER_ID=purchase.PURCHASE_ORDER_ID
                      AND item.PRODUCT_ID=purchase.PRODUCT_ID);
            """, transaction: transaction);
        connection.Execute("""
            INSERT INTO SYS_SQLITE_RECONCILIATION (RECONCILIATION_ID, APPLIED_AT)
            VALUES (@Marker, @AppliedAt)
            """, new { Marker, AppliedAt = DateTime.UtcNow.ToString("O") }, transaction);
    }
}
