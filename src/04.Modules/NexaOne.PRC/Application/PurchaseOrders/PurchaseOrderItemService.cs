using NexaOne.Common;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Application.PurchaseOrders;

internal sealed class PurchaseOrderItemService(IPurchaseOrderItemStore store) : IPurchaseOrderItemBridge
{
    internal const decimal MaximumQuantity = 99999999999999.9999m; // DECIMAL(18,4)

    public async Task<Result<PurchaseOrderItemTotal>> SaveDraftItemAsync(
        string? purchaseOrderId, string? productId, decimal quantity, string? actorId,
        CancellationToken ct = default)
    {
        if (!ValidId(purchaseOrderId) || !ValidId(productId) || !ValidId(actorId)
            || quantity <= 0 || quantity > MaximumQuantity
            || decimal.Round(quantity, 4) != quantity)
            return InvalidInput();

        var id = purchaseOrderId!.Trim();
        return FromWrite(id, await store.TrySaveAsync(
            id, productId!.Trim(), quantity, actorId!.Trim(), ct));
    }

    public async Task<Result<PurchaseOrderItemTotal>> DeleteDraftItemAsync(
        string? purchaseOrderId, string? productId, string? actorId,
        CancellationToken ct = default)
    {
        if (!ValidId(purchaseOrderId) || !ValidId(productId) || !ValidId(actorId))
            return InvalidInput();

        var id = purchaseOrderId!.Trim();
        return FromWrite(id, await store.TryDeleteAsync(
            id, productId!.Trim(), actorId!.Trim(), ct));
    }

    public async Task<Result<IReadOnlyList<PurchaseOrderItem>>> ListAsync(
        string? purchaseOrderId, CancellationToken ct = default)
    {
        if (!ValidId(purchaseOrderId))
            return Result.Failure<IReadOnlyList<PurchaseOrderItem>>(Error.Validation(
                "PRC_ITEM_ORDER_ID_INVALID", "발주 ID를 확인하세요."));
        var items = await store.ListAsync(purchaseOrderId!.Trim(), ct);
        return items is null
            ? Result.Failure<IReadOnlyList<PurchaseOrderItem>>(OrderNotFound())
            : Result.Success(items);
    }

    private static Result<PurchaseOrderItemTotal> FromWrite(
        string purchaseOrderId, PurchaseItemWriteResult result) => result.Outcome switch
    {
        PurchaseItemWriteOutcome.Applied => Result.Success(new PurchaseOrderItemTotal(
            purchaseOrderId, result.OrderQuantity)),
        PurchaseItemWriteOutcome.OrderNotFound => Result.Failure<PurchaseOrderItemTotal>(OrderNotFound()),
        PurchaseItemWriteOutcome.ItemNotFound => Result.Failure<PurchaseOrderItemTotal>(Error.NotFound(
            "PRC_ITEM_NOT_FOUND", "발주 품목을 찾을 수 없습니다.")),
        PurchaseItemWriteOutcome.NotEditable => Result.Failure<PurchaseOrderItemTotal>(Error.Conflict(
            "PRC_ITEM_NOT_EDITABLE", "미보류 Draft 발주의 품목만 편집할 수 있습니다.")),
        PurchaseItemWriteOutcome.InvalidProduct => Result.Failure<PurchaseOrderItemTotal>(Error.Validation(
            "PRC_ITEM_PRODUCT_INVALID", "유효한 품목이 필요합니다.")),
        PurchaseItemWriteOutcome.QuantityOverflow => Result.Failure<PurchaseOrderItemTotal>(Error.Validation(
            "PRC_ITEM_TOTAL_OVERFLOW", "발주 품목 수량 합계가 허용 범위를 넘습니다.")),
        _ => throw new InvalidOperationException("Unknown purchase item outcome."),
    };

    private static Result<PurchaseOrderItemTotal> InvalidInput() =>
        Result.Failure<PurchaseOrderItemTotal>(Error.Validation(
            "PRC_ITEM_COMMAND_INVALID", "발주·품목 ID, 양수 수량, 실행자를 확인하세요."));

    private static Error OrderNotFound() =>
        Error.NotFound("PRC_ORDER_NOT_FOUND", "발주를 찾을 수 없습니다.");

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 50;
}
