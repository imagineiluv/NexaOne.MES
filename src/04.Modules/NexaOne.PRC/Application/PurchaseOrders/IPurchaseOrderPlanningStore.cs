namespace NexaOne.PRC.Application.PurchaseOrders;

/// <summary>구매오더 계획 유스케이스가 요구하는 PRC 소유 저장 포트입니다.</summary>
internal interface IPurchaseOrderPlanningStore
{
    Task<IReadOnlyList<PurchaseOrderScheduledReceipt>> GetScheduledReceiptsAsync(
        CancellationToken ct = default);

    Task<PurchaseOrderPlanningSnapshot?> FindAsync(
        string purchaseOrderId,
        CancellationToken ct = default);

    Task<PurchaseOrderInsertOutcome> TryInsertAsync(
        PurchaseOrderDraft draft,
        CancellationToken ct = default);
}

internal enum PurchaseOrderInsertOutcome
{
    Created,
    IdentityConflict,
}

internal sealed record PurchaseOrderScheduledReceipt(
    string ProductId,
    decimal Quantity,
    DateTime? IncomingDate);

internal sealed record PurchaseOrderPlanningSnapshot(
    string PurchaseOrderId,
    string PlantId,
    string? PurchaseOrderName,
    DateTime? IncomingDate,
    decimal Quantity,
    string ProductId,
    string Status,
    string? Description);

internal sealed record PurchaseOrderDraft(
    string PurchaseOrderId,
    string PlantId,
    string PurchaseOrderName,
    DateTime OrderDate,
    DateTime? IncomingDate,
    decimal Quantity,
    string ProductId,
    string Description,
    string ExecutedBy);
