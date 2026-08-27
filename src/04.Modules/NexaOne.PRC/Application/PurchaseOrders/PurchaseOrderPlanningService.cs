using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Application.PurchaseOrders;

/// <summary>
/// 예정 입고 조회와 구매오더 멱등 생성을 조정하고 PRC의 명령 동등성 규칙을 소유합니다.
/// </summary>
internal sealed class PurchaseOrderPlanningService
{
    private readonly IPurchaseOrderPlanningStore _store;

    public PurchaseOrderPlanningService(IPurchaseOrderPlanningStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<IReadOnlyList<MrpPurchaseReceipt>> GetScheduledReceiptsAsync(
        CancellationToken ct = default)
    {
        var receipts = await _store.GetScheduledReceiptsAsync(ct);
        return receipts
            .Select(static receipt => new MrpPurchaseReceipt(
                receipt.ProductId,
                receipt.Quantity,
                receipt.IncomingDate))
            .ToArray();
    }

    public async Task<PurchaseOrderEnsureResult> EnsureMrpPurchaseOrderAsync(
        MrpPurchaseOrderRequest request,
        CancellationToken ct = default)
    {
        Validate(request);

        var existing = await _store.FindAsync(request.PurchaseOrderId, ct);
        if (existing is not null)
        {
            EnsureSameCommand(existing, request);
            return Existing(request);
        }

        var outcome = await _store.TryInsertAsync(ToDraft(request), ct);
        if (outcome == PurchaseOrderInsertOutcome.Created)
            return new PurchaseOrderEnsureResult(request.PurchaseOrderId, true);

        if (outcome != PurchaseOrderInsertOutcome.IdentityConflict)
            throw new InvalidOperationException($"Unsupported purchase-order insert outcome '{outcome}'.");

        // Only the storage adapter can classify a provider-specific identity race. The application
        // then re-reads with the caller's token and applies the same command-equivalence rule.
        existing = await _store.FindAsync(request.PurchaseOrderId, ct);
        if (existing is null)
        {
            throw new InvalidOperationException(
                $"Purchase order id '{request.PurchaseOrderId}' conflicted during creation, " +
                "but the winning command could not be loaded.");
        }

        EnsureSameCommand(existing, request);
        return Existing(request);
    }

    private static PurchaseOrderEnsureResult Existing(MrpPurchaseOrderRequest request)
        => new(request.PurchaseOrderId, false);

    private static PurchaseOrderDraft ToDraft(MrpPurchaseOrderRequest request)
        => new(
            request.PurchaseOrderId,
            request.PlantId,
            request.PurchaseOrderName,
            request.OrderDate,
            request.IncomingDate,
            request.Quantity,
            request.ProductId,
            request.Description,
            request.ExecutedBy);

    private static void Validate(MrpPurchaseOrderRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.PurchaseOrderId)
            || string.IsNullOrWhiteSpace(request.PlantId)
            || string.IsNullOrWhiteSpace(request.ProductId)
            || string.IsNullOrWhiteSpace(request.ExecutedBy)
            || request.Quantity <= 0)
        {
            throw new ArgumentException(
                "Purchase order id, plant, product, positive quantity and actor are required.",
                nameof(request));
        }
    }

    private static void EnsureSameCommand(
        PurchaseOrderPlanningSnapshot existing,
        MrpPurchaseOrderRequest request)
    {
        var same = string.Equals(existing.PlantId, request.PlantId, StringComparison.OrdinalIgnoreCase)
                   && string.Equals(existing.ProductId, request.ProductId, StringComparison.OrdinalIgnoreCase)
                   && existing.Quantity == request.Quantity
                   && string.Equals(
                       existing.PurchaseOrderName ?? string.Empty,
                       request.PurchaseOrderName,
                       StringComparison.Ordinal)
                   // OrderDate is execution metadata generated anew by MRP recovery. Stable
                   // planning content, rather than retry time, defines command equivalence.
                   && Nullable.Equals(existing.IncomingDate, request.IncomingDate)
                   && string.Equals(
                       existing.Description ?? string.Empty,
                       request.Description,
                       StringComparison.Ordinal)
                   && IsReplayableStatus(existing.Status);
        if (!same)
        {
            throw new InvalidOperationException(
                $"Purchase order id '{request.PurchaseOrderId}' is already owned by a different command.");
        }
    }

    private static bool IsReplayableStatus(string? status)
        => status is not null
           && (string.Equals(status, "Ordered", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "Incoming", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "Closed", StringComparison.OrdinalIgnoreCase));
}
