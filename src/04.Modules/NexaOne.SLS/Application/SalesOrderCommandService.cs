using NexaOne.Common;
using NexaOne.ServiceContracts.Sls;

namespace NexaOne.SLS.Application;

/// <summary>화면·호스트가 공유하는 수주 초안 및 상태 전이 계약을 집행합니다.</summary>
internal sealed class SalesOrderCommandService(ISalesOrderCommandStore store) : ISalesOrderCommandBridge
{
    private const decimal MaximumQuantity = 99999999999999.9999m; // DECIMAL(18,4)

    public async Task<Result<SalesOrderCommandState>> SaveDraftAsync(
        SalesOrderDraftCommand command, CancellationToken ct = default)
    {
        if (command is null || !ValidId(command.SalesOrderId) || !ValidId(command.PlantId)
            || !ValidId(command.CustomerId) || !ValidId(command.ProductId) || !ValidId(command.ActorId)
            || !ValidName(command.SalesOrderName) || command.PlanEndDate is null or { Year: 1 }
            || (command.PlanStartDate is { } start && command.PlanEndDate < start)
            || command.PlanQty <= 0 || command.PlanQty > MaximumQuantity
            || decimal.Round(command.PlanQty, 4) != command.PlanQty)
            return Result.Failure<SalesOrderCommandState>(Error.Validation(
                "SLS_ORDER_DRAFT_INVALID", "수주 ID·공장·고객·품목·납기·양수 수량·실행자를 확인하세요."));

        var id = command.SalesOrderId!.Trim();
        var draft = new SalesOrderDraft(
            id, string.IsNullOrWhiteSpace(command.SalesOrderName) ? id : command.SalesOrderName.Trim(),
            command.PlantId!.Trim(), command.CustomerId!.Trim(), command.ProductId!.Trim(),
            command.PlanStartDate, command.PlanEndDate!.Value, command.PlanQty, command.ActorId!.Trim());
        return await store.TrySaveDraftAsync(draft, ct) switch
        {
            SalesOrderDraftOutcome.Saved => Result.Success(new SalesOrderCommandState(id, "Draft")),
            SalesOrderDraftOutcome.InvalidReference => Result.Failure<SalesOrderCommandState>(Error.Validation(
                "SLS_ORDER_REFERENCE_INVALID", "유효한 공장·활성 고객·유효 품목이 필요합니다.")),
            SalesOrderDraftOutcome.NotEditable => Result.Failure<SalesOrderCommandState>(Error.Conflict(
                "SLS_ORDER_NOT_EDITABLE", "미보류 Draft 수주만 편집할 수 있습니다.")),
            SalesOrderDraftOutcome.IdentityConflict => Result.Failure<SalesOrderCommandState>(Error.Conflict(
                "SLS_ORDER_ID_CONFLICT", "이미 사용 중인 수주 ID입니다.")),
            _ => throw new InvalidOperationException("Unknown sales-order draft outcome."),
        };
    }

    public async Task<Result> DeleteDraftAsync(
        string? salesOrderId, string? actorId, CancellationToken ct = default)
    {
        if (!ValidId(salesOrderId) || !ValidId(actorId)) return InvalidCommand();
        return await store.TryDeleteDraftAsync(salesOrderId!.Trim(), actorId!.Trim(), ct) switch
        {
            SalesOrderChangeOutcome.Applied => Result.Success(),
            SalesOrderChangeOutcome.NotFound => OrderNotFound(),
            SalesOrderChangeOutcome.NotAllowed => Result.Failure(Error.Conflict(
                "SLS_ORDER_NOT_DELETABLE", "미보류·미연결 Draft 수주만 삭제할 수 있습니다.")),
            _ => throw new InvalidOperationException("Unknown sales-order deletion outcome."),
        };
    }

    public Task<Result<SalesOrderCommandState>> ConfirmAsync(
        string? salesOrderId, string? actorId, CancellationToken ct = default)
        => TransitionAsync(salesOrderId, actorId, "Confirmed", store.TryConfirmAsync, ct);

    public Task<Result<SalesOrderCommandState>> CloseAsync(
        string? salesOrderId, string? actorId, CancellationToken ct = default)
        => TransitionAsync(salesOrderId, actorId, "Closed", store.TryCloseAsync, ct);

    private static async Task<Result<SalesOrderCommandState>> TransitionAsync(
        string? salesOrderId, string? actorId, string targetStatus,
        Func<string, string, CancellationToken, Task<SalesOrderChangeOutcome>> transition,
        CancellationToken ct)
    {
        if (!ValidId(salesOrderId) || !ValidId(actorId))
            return Result.Failure<SalesOrderCommandState>(Error.Validation(
                "SLS_ORDER_COMMAND_INVALID", "수주 ID와 실행자를 확인하세요."));
        var id = salesOrderId!.Trim();
        return await transition(id, actorId!.Trim(), ct) switch
        {
            SalesOrderChangeOutcome.Applied => Result.Success(new SalesOrderCommandState(id, targetStatus)),
            SalesOrderChangeOutcome.NotFound => Result.Failure<SalesOrderCommandState>(Error.NotFound(
                "SLS_ORDER_NOT_FOUND", "수주를 찾을 수 없습니다.")),
            SalesOrderChangeOutcome.NotAllowed => Result.Failure<SalesOrderCommandState>(Error.Conflict(
                "SLS_ORDER_TRANSITION_CONFLICT", "현재 상태 또는 보류 상태에서는 수주를 전이할 수 없습니다.")),
            _ => throw new InvalidOperationException("Unknown sales-order transition outcome."),
        };
    }

    private static Result InvalidCommand() => Result.Failure(Error.Validation(
        "SLS_ORDER_COMMAND_INVALID", "수주 ID와 실행자를 확인하세요."));

    private static Result OrderNotFound() => Result.Failure(Error.NotFound(
        "SLS_ORDER_NOT_FOUND", "수주를 찾을 수 없습니다."));

    private static bool ValidId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 50;

    private static bool ValidName(string? value)
        => value is null || value.Trim().Length <= 200;
}
