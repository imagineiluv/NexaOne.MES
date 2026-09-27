using NexaOne.Common;
using NexaOne.ServiceContracts.Sls;

namespace NexaOne.SLS.Application;

/// <summary>판매요청의 입력 계약과 상태 전이 결과를 정의합니다. 저장소가 원자성·상태 가드를 집행합니다.</summary>
internal sealed class SalesRequestService : ISalesRequestBridge
{
    private const decimal MaximumQuantity = 99999999999999.9999m; // DECIMAL(18,4)
    private readonly ISalesRequestStore _store;

    public SalesRequestService(ISalesRequestStore store)
        => _store = store ?? throw new ArgumentNullException(nameof(store));

    public async Task<Result<SalesRequestState>> CreateDraftAsync(
        SalesRequestDraftCommand command, CancellationToken ct = default)
    {
        if (command is null || !ValidId(command.SalesRequestId) || !ValidId(command.CustomerId)
            || !ValidId(command.ProductId) || !ValidId(command.ActorId)
            || !ValidName(command.SalesRequestName) || command.RequestDate == default
            || command.RequestQty <= 0 || command.RequestQty > MaximumQuantity
            || decimal.Round(command.RequestQty, 4) != command.RequestQty)
        {
            return Result.Failure<SalesRequestState>(Error.Validation(
                "SLS_REQUEST_INVALID", "판매요청 ID·고객·품목·일자·양수 수량·실행자를 확인하세요."));
        }

        var id = command.SalesRequestId!.Trim();
        var draft = new SalesRequestDraft(
            id, NameOrId(command.SalesRequestName, id), command.CustomerId!.Trim(),
            command.ProductId!.Trim(), command.RequestDate, command.RequestQty, command.ActorId!.Trim());

        return await _store.TryCreateDraftAsync(draft, ct) switch
        {
            SalesRequestDraftInsertOutcome.Created => Result.Success(new SalesRequestState(id, "Draft", null)),
            SalesRequestDraftInsertOutcome.InvalidReference => Result.Failure<SalesRequestState>(Error.Validation(
                "SLS_REQUEST_REFERENCE_INVALID", "활성 고객과 유효한 품목이 필요합니다.")),
            SalesRequestDraftInsertOutcome.IdentityConflict => Result.Failure<SalesRequestState>(Error.Conflict(
                "SLS_REQUEST_ID_CONFLICT", "이미 사용 중인 판매요청 ID입니다.")),
            _ => throw new InvalidOperationException("Unknown sales-request draft insert outcome."),
        };
    }

    public async Task<Result<SalesRequestState>> ReceiveAsync(
        SalesRequestReceiptCommand command, CancellationToken ct = default)
    {
        if (command is null || !ValidId(command.SalesRequestId) || !ValidId(command.SalesOrderId)
            || !ValidId(command.PlantId) || !ValidId(command.ActorId)
            || !ValidName(command.SalesOrderName) || command.PlanStartDate == default
            || command.PlanEndDate == default || command.PlanEndDate < command.PlanStartDate)
        {
            return Result.Failure<SalesRequestState>(Error.Validation(
                "SLS_RECEIPT_INVALID", "요청·수주·공장 ID와 유효한 계획 기간·실행자가 필요합니다."));
        }

        var id = command.SalesRequestId!.Trim();
        var orderId = command.SalesOrderId!.Trim();
        var receipt = new SalesRequestReceipt(
            id, orderId, command.PlantId!.Trim(), NameOrId(command.SalesOrderName, orderId),
            command.PlanStartDate, command.PlanEndDate, command.ActorId!.Trim());

        return await _store.TryReceiveAsync(receipt, ct) switch
        {
            SalesRequestReceiptOutcome.Received => Result.Success(new SalesRequestState(id, "Confirmed", orderId)),
            SalesRequestReceiptOutcome.RequestNotFound => Result.Failure<SalesRequestState>(Error.NotFound(
                "SLS_REQUEST_NOT_FOUND", "판매요청을 찾을 수 없습니다.")),
            SalesRequestReceiptOutcome.PlantNotFound => Result.Failure<SalesRequestState>(Error.Validation(
                "SLS_PLANT_NOT_FOUND", "공장을 찾을 수 없습니다.")),
            SalesRequestReceiptOutcome.InvalidReference => Result.Failure<SalesRequestState>(Error.Validation(
                "SLS_REQUEST_REFERENCE_INVALID", "활성 고객과 유효한 품목이 필요합니다.")),
            SalesRequestReceiptOutcome.NotReceivable => Result.Failure<SalesRequestState>(Error.Conflict(
                "SLS_REQUEST_NOT_RECEIVABLE", "초안 상태의 미연결 판매요청만 수령할 수 있습니다.")),
            SalesRequestReceiptOutcome.OrderIdentityConflict => Result.Failure<SalesRequestState>(Error.Conflict(
                "SLS_ORDER_ID_CONFLICT", "이미 사용 중인 수주 ID입니다.")),
            _ => throw new InvalidOperationException("Unknown sales-request receipt outcome."),
        };
    }

    public async Task<Result<SalesRequestState>> WithdrawAsync(
        SalesRequestWithdrawCommand command, CancellationToken ct = default)
    {
        if (command is null || !ValidId(command.SalesRequestId) || !ValidId(command.ActorId))
        {
            return Result.Failure<SalesRequestState>(Error.Validation(
                "SLS_WITHDRAW_INVALID", "판매요청 ID와 실행자를 확인하세요."));
        }

        var id = command.SalesRequestId!.Trim();
        return await _store.TryWithdrawAsync(id, command.ActorId!.Trim(), ct) switch
        {
            SalesRequestWithdrawOutcome.Withdrawn => Result.Success(new SalesRequestState(id, "Cancelled", null)),
            SalesRequestWithdrawOutcome.RequestNotFound => Result.Failure<SalesRequestState>(Error.NotFound(
                "SLS_REQUEST_NOT_FOUND", "판매요청을 찾을 수 없습니다.")),
            SalesRequestWithdrawOutcome.NotWithdrawable => Result.Failure<SalesRequestState>(Error.Conflict(
                "SLS_REQUEST_NOT_WITHDRAWABLE", "초안 상태의 미연결 판매요청만 철회할 수 있습니다.")),
            _ => throw new InvalidOperationException("Unknown sales-request withdraw outcome."),
        };
    }

    private static bool ValidId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 50;

    private static bool ValidName(string? value)
        => value is null || value.Trim().Length <= 200;

    private static string NameOrId(string? value, string id)
        => string.IsNullOrWhiteSpace(value) ? id : value.Trim();
}
