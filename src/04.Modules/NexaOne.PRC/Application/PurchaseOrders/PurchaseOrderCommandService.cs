using NexaOne.Common;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.PRC.Application.PurchaseOrders;

internal sealed class PurchaseOrderCommandService(IPurchaseOrderCommandStore store)
    : IPurchaseOrderCommandBridge
{
    private const decimal MaximumQuantity = 99999999999999.9999m; // DECIMAL(18,4)

    public async Task<Result<PurchaseOrderCommandState>> SaveDraftAsync(
        PurchaseOrderDraftCommand command, CancellationToken ct = default)
    {
        if (command is null || !ValidId(command.PurchaseOrderId) || !ValidId(command.PlantId)
            || !ValidId(command.ActorId) || !ValidOptional(command.VendorId, 50)
            || !ValidOptional(command.PurchaseOrderName, 200)
            || command.OrderQuantity < 0 || command.OrderQuantity > MaximumQuantity
            || decimal.Round(command.OrderQuantity, 4) != command.OrderQuantity)
            return Result.Failure<PurchaseOrderCommandState>(Error.Validation(
                "PRC_ORDER_DRAFT_INVALID", "발주 ID·공장·수량·실행자를 확인하세요."));

        var draft = new PurchaseOrderHeaderDraft(
            command.PurchaseOrderId!.Trim(), command.PlantId!.Trim(),
            TrimOrNull(command.PurchaseOrderName), TrimOrNull(command.VendorId),
            command.OrderQuantity, command.ActorId!.Trim());
        return await store.TrySaveDraftAsync(draft, ct) switch
        {
            PurchaseOrderDraftOutcome.Saved => Result.Success(
                new PurchaseOrderCommandState(draft.PurchaseOrderId, "Draft")),
            PurchaseOrderDraftOutcome.NotEditable => Result.Failure<PurchaseOrderCommandState>(Error.Conflict(
                "PRC_ORDER_NOT_EDITABLE", "미보류 Draft 발주만 편집할 수 있습니다.")),
            PurchaseOrderDraftOutcome.IdentityConflict => Result.Failure<PurchaseOrderCommandState>(Error.Conflict(
                "PRC_ORDER_ID_CONFLICT", "이미 사용 중인 발주 ID입니다.")),
            _ => throw new InvalidOperationException("Unknown purchase-order draft outcome."),
        };
    }

    public async Task<Result> DeleteDraftAsync(
        string? purchaseOrderId, string? actorId, CancellationToken ct = default)
    {
        if (!ValidId(purchaseOrderId) || !ValidId(actorId))
            return Result.Failure(Error.Validation(
                "PRC_ORDER_COMMAND_INVALID", "발주 ID와 실행자를 확인하세요."));
        return await store.TryDeleteDraftAsync(purchaseOrderId!.Trim(), actorId!.Trim(), ct) switch
        {
            PurchaseOrderCommandOutcome.Applied => Result.Success(),
            PurchaseOrderCommandOutcome.NotFound => Result.Failure(Error.NotFound(
                "PRC_ORDER_NOT_FOUND", "발주를 찾을 수 없습니다.")),
            PurchaseOrderCommandOutcome.NotAllowed => Result.Failure(Error.Conflict(
                "PRC_ORDER_NOT_DELETABLE", "입고가 없는 미보류 Draft 발주만 삭제할 수 있습니다.")),
            _ => throw new InvalidOperationException("Unknown purchase-order deletion outcome."),
        };
    }

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

    private static bool ValidOptional(string? value, int maximumLength) =>
        value is null || value.Trim().Length <= maximumLength;

    private static string? TrimOrNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
