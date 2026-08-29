using System.Data.Common;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using NexaOne.PRC.Application.PurchaseOrders;

namespace NexaOne.PRC.Infrastructure;

/// <summary>PRC 구매오더 원장의 SQL과 공급자별 식별자 충돌 분류를 캡슐화합니다.</summary>
internal sealed class PurchaseOrderPlanningRepository : QueryRepository, IPurchaseOrderPlanningStore
{
    private readonly ServiceObjectProcessor _processor;

    public PurchaseOrderPlanningRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<IReadOnlyList<PurchaseOrderScheduledReceipt>> GetScheduledReceiptsAsync(
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<ReceiptRow>(
            "SELECT PRODUCT_ID AS ProductId, ORDER_QTY AS Quantity, INCOMING_DATE AS IncomingDate " +
            "FROM PRC_PURCHASE_ORDER " +
            "WHERE STATUS IN ('Ordered', 'Incoming') AND PRODUCT_ID IS NOT NULL",
            null,
            ct);
        return rows
            .Select(static row => new PurchaseOrderScheduledReceipt(
                row.ProductId,
                row.Quantity,
                AsDate(row.IncomingDate)))
            .ToArray();
    }

    public async Task<PurchaseOrderPlanningSnapshot?> FindAsync(
        string purchaseOrderId,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<PurchaseOrderRow>(
            "SELECT PURCHASE_ORDER_ID AS PurchaseOrderId, PLANT_ID AS PlantId, " +
            "PURCHASE_ORDER_NAME AS PurchaseOrderName, INCOMING_DATE AS IncomingDate, " +
            "ORDER_QTY AS Quantity, PRODUCT_ID AS ProductId, " +
            "STATUS AS Status, DESCRIPTION AS Description " +
            "FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID = @purchaseOrderId",
            new { purchaseOrderId },
            ct);
        return row is null
            ? null
            : new PurchaseOrderPlanningSnapshot(
                row.PurchaseOrderId,
                row.PlantId,
                row.PurchaseOrderName,
                AsDate(row.IncomingDate),
                row.Quantity,
                row.ProductId,
                row.Status,
                row.Description);
    }

    public async Task<PurchaseOrderInsertOutcome> TryInsertAsync(
        PurchaseOrderDraft draft,
        CancellationToken ct = default)
    {
        const string sql =
            "INSERT INTO PRC_PURCHASE_ORDER (PURCHASE_ORDER_ID, PLANT_ID, PURCHASE_ORDER_NAME, " +
            "ORDER_DATE, INCOMING_DATE, ORDER_QTY, PRODUCT_ID, STATUS, DESCRIPTION, CREATED_BY, UPDATED_BY) " +
            "VALUES (@PurchaseOrderId, @PlantId, @PurchaseOrderName, @OrderDate, @IncomingDate, @Quantity, " +
            "@ProductId, 'Ordered', @Description, @ExecutedBy, @ExecutedBy)";
        try
        {
            await _processor.ExecuteAsync(sql, draft, ct);
            return PurchaseOrderInsertOutcome.Created;
        }
        catch (DbException exception) when (IsExpectedPurchaseOrderIdentityRace(exception))
        {
            return PurchaseOrderInsertOutcome.IdentityConflict;
        }
    }

    private static bool IsExpectedPurchaseOrderIdentityRace(DbException exception)
    {
        var isUniqueViolation = exception switch
        {
            SqliteException sqlite =>
                sqlite.SqliteErrorCode == 19
                && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
            _ when string.Equals(
                    exception.GetType().FullName,
                    "Microsoft.Data.SqlClient.SqlException",
                    StringComparison.Ordinal)
                => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
                   && number is 2601 or 2627,
            _ => false,
        };
        if (!isUniqueViolation) return false;

        return exception.Message.Contains(
                   "PK_PRC_PURCHASE_ORDER",
                   StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "PRC_PURCHASE_ORDER.PURCHASE_ORDER_ID",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static DateTime? AsDate(object? value) => value switch
    {
        null => null,
        DateTime date => date,
        string text when DateTime.TryParse(text, out var date) => date,
        _ => null,
    };

    private sealed class ReceiptRow
    {
        public string ProductId { get; set; } = string.Empty;
        public decimal Quantity { get; set; }
        public object? IncomingDate { get; set; }
    }

    private sealed class PurchaseOrderRow
    {
        public string PurchaseOrderId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string? PurchaseOrderName { get; set; }
        public object? IncomingDate { get; set; }
        public decimal Quantity { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
