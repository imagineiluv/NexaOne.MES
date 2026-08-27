using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Infrastructure;

/// <summary>PRC 구매오더 원장의 MRP 조회·멱등 생성 adapter입니다.</summary>
public sealed class PurchaseOrderPlanningBridge : QueryRepository, IPurchaseOrderPlanningBridge
{
    private readonly ServiceObjectProcessor _processor;

    public PurchaseOrderPlanningBridge(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<ReceiptRow>(
            "SELECT PRODUCT_ID AS ProductId, ORDER_QTY AS Quantity, INCOMING_DATE AS IncomingDate " +
            "FROM PRC_PURCHASE_ORDER " +
            "WHERE STATUS IN ('Ordered', 'Incoming') AND PRODUCT_ID IS NOT NULL",
            null,
            ct);
        return rows.Select(static row => new MrpPurchaseReceipt(
            row.ProductId,
            row.Quantity,
            AsDate(row.IncomingDate))).ToArray();
    }

    public async Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
        MrpPurchaseOrderRequest request,
        CancellationToken ct = default)
    {
        Validate(request);
        var existing = await FindAsync(request.PurchaseOrderId, ct);
        if (existing is not null)
        {
            EnsureSameCommand(existing, request);
            return new PurchaseOrderEnsureResult(request.PurchaseOrderId, false);
        }

        const string sql =
            "INSERT INTO PRC_PURCHASE_ORDER (PURCHASE_ORDER_ID, PLANT_ID, PURCHASE_ORDER_NAME, " +
            "ORDER_DATE, INCOMING_DATE, ORDER_QTY, PRODUCT_ID, STATUS, DESCRIPTION, CREATED_BY, UPDATED_BY) " +
            "VALUES (@PurchaseOrderId, @PlantId, @PurchaseOrderName, @OrderDate, @IncomingDate, @Quantity, " +
            "@ProductId, 'Ordered', @Description, @ExecutedBy, @ExecutedBy)";
        try
        {
            await _processor.ExecuteAsync(sql, request, ct);
            return new PurchaseOrderEnsureResult(request.PurchaseOrderId, true);
        }
        catch
        {
            // A concurrent retry can win the primary-key race. Only suppress that failure when the
            // persisted command is byte-for-byte equivalent at the business boundary.
            existing = await FindAsync(request.PurchaseOrderId, CancellationToken.None);
            if (existing is null) throw;
            EnsureSameCommand(existing, request);
            return new PurchaseOrderEnsureResult(request.PurchaseOrderId, false);
        }
    }

    private Task<PurchaseOrderRow?> FindAsync(string purchaseOrderId, CancellationToken ct) =>
        QueryFirstOrDefaultAsync<PurchaseOrderRow>(
            "SELECT PURCHASE_ORDER_ID AS PurchaseOrderId, PLANT_ID AS PlantId, " +
            "PURCHASE_ORDER_NAME AS PurchaseOrderName, ORDER_DATE AS OrderDate, " +
            "INCOMING_DATE AS IncomingDate, ORDER_QTY AS Quantity, PRODUCT_ID AS ProductId, " +
            "STATUS AS Status, DESCRIPTION AS Description " +
            "FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID = @purchaseOrderId",
            new { purchaseOrderId },
            ct);

    private static void Validate(MrpPurchaseOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PurchaseOrderId) ||
            string.IsNullOrWhiteSpace(request.PlantId) ||
            string.IsNullOrWhiteSpace(request.ProductId) ||
            string.IsNullOrWhiteSpace(request.ExecutedBy) ||
            request.Quantity <= 0)
        {
            throw new ArgumentException("Purchase order id, plant, product, positive quantity and actor are required.",
                nameof(request));
        }
    }

    private static void EnsureSameCommand(PurchaseOrderRow row, MrpPurchaseOrderRequest request)
    {
        var same = string.Equals(row.PlantId, request.PlantId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(row.ProductId, request.ProductId, StringComparison.OrdinalIgnoreCase)
                   && row.Quantity == request.Quantity
                   && string.Equals(row.PurchaseOrderName ?? string.Empty, request.PurchaseOrderName,
                       StringComparison.Ordinal)
                   && Nullable.Equals(AsDate(row.IncomingDate), request.IncomingDate)
                   && string.Equals(row.Description ?? string.Empty, request.Description, StringComparison.Ordinal)
                   && row.Status is not null
                   && (string.Equals(row.Status, "Ordered", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(row.Status, "Incoming", StringComparison.OrdinalIgnoreCase)
                       || string.Equals(row.Status, "Closed", StringComparison.OrdinalIgnoreCase));
        if (!same)
        {
            throw new InvalidOperationException(
                $"Purchase order id '{request.PurchaseOrderId}' is already owned by a different command.");
        }
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
        public object? OrderDate { get; set; }
        public object? IncomingDate { get; set; }
        public decimal Quantity { get; set; }
        public string ProductId { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Description { get; set; }
    }
}
