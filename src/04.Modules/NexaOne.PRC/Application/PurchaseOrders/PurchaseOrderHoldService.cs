using NexaOne.Common;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Application.PurchaseOrders;

internal sealed class PurchaseOrderHoldService(IPurchaseOrderHoldStore store)
    : IPurchaseOrderHoldBridge
{
    public Task<Result<PurchaseOrderHoldState>> HoldAsync(string? purchaseOrderId, string? actorId,
        CancellationToken ct = default) => ChangeAsync(
        purchaseOrderId, actorId, true, store.TryHoldAsync, ct);

    public Task<Result<PurchaseOrderHoldState>> ReleaseAsync(string? purchaseOrderId, string? actorId,
        CancellationToken ct = default) => ChangeAsync(
        purchaseOrderId, actorId, false, store.TryReleaseAsync, ct);

    private static async Task<Result<PurchaseOrderHoldState>> ChangeAsync(
        string? purchaseOrderId, string? actorId, bool hold,
        Func<string, string, CancellationToken, Task<PurchaseOrderHoldOutcome>> change,
        CancellationToken ct)
    {
        if (!ValidId(purchaseOrderId) || !ValidId(actorId))
            return Result.Failure<PurchaseOrderHoldState>(Error.Validation(
                "PRC_HOLD_COMMAND_INVALID", "발주 ID와 실행자를 확인하세요."));

        var id = purchaseOrderId!.Trim();
        return await change(id, actorId!.Trim(), ct) switch
        {
            PurchaseOrderHoldOutcome.Applied => Result.Success(new PurchaseOrderHoldState(id, hold)),
            PurchaseOrderHoldOutcome.NotFound => Result.Failure<PurchaseOrderHoldState>(Error.NotFound(
                "PRC_ORDER_NOT_FOUND", "발주를 찾을 수 없습니다.")),
            PurchaseOrderHoldOutcome.NotAllowed => Result.Failure<PurchaseOrderHoldState>(Error.Conflict(
                "PRC_HOLD_TRANSITION_CONFLICT", "현재 상태에서는 발주 보류 상태를 변경할 수 없습니다.")),
            _ => throw new InvalidOperationException("Unknown purchase-order hold outcome."),
        };
    }

    private static bool ValidId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 50;
}
