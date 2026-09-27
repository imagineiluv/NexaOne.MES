using NexaOne.Common;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Application.PurchaseOrders;

internal sealed class PurchaseOrderCommandService(IPurchaseOrderCommandStore store)
    : IPurchaseOrderCommandBridge
{
    public Task<Result<PurchaseOrderCommandState>> OrderAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default) =>
        ChangeAsync(purchaseOrderId, actorId, "Ordered", store.TryOrderAsync, ct);

    public Task<Result<PurchaseOrderCommandState>> CloseAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default) =>
        ChangeAsync(purchaseOrderId, actorId, "Closed", store.TryCloseAsync, ct);

    private static async Task<Result<PurchaseOrderCommandState>> ChangeAsync(
        string? purchaseOrderId, string? actorId, string targetStatus,
        Func<string, string, CancellationToken, Task<PurchaseOrderCommandOutcome>> change,
        CancellationToken ct)
    {
        if (!ValidId(purchaseOrderId) || !ValidId(actorId))
            return Result.Failure<PurchaseOrderCommandState>(Error.Validation(
                "PRC_ORDER_COMMAND_INVALID", "발주 ID와 실행자를 확인하세요."));

        var id = purchaseOrderId!.Trim();
        return await change(id, actorId!.Trim(), ct) switch
        {
            PurchaseOrderCommandOutcome.Applied => Result.Success(
                new PurchaseOrderCommandState(id, targetStatus)),
            PurchaseOrderCommandOutcome.NotFound => Result.Failure<PurchaseOrderCommandState>(Error.NotFound(
                "PRC_ORDER_NOT_FOUND", "발주를 찾을 수 없습니다.")),
            PurchaseOrderCommandOutcome.NotAllowed => Result.Failure<PurchaseOrderCommandState>(Error.Conflict(
                "PRC_ORDER_TRANSITION_CONFLICT", "현재 상태·보류·품목 입고 수량을 확인하세요.")),
            _ => throw new InvalidOperationException("Unknown purchase-order transition outcome."),
        };
    }

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 50;
}
