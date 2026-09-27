using NexaOne.Common;
using NexaOne.ServiceContracts.Sls;

namespace NexaOne.SLS.Application;

/// <summary>출하요청의 입력·결과 계약을 정의하고 원자적 저장은 SLS 저장소에 위임합니다.</summary>
internal sealed class SalesOrderDeliveryService(ISalesOrderDeliveryStore store) : ISalesOrderDeliveryBridge
{
    public async Task<Result<SalesOrderDeliveryState>> RequestDeliveryAsync(
        SalesOrderDeliveryCommand command, CancellationToken ct = default)
    {
        if (command is null || !ValidId(command.SalesOrderId) || !ValidId(command.DeliveryOrderId)
            || !ValidId(command.DeliveryItemId) || !ValidId(command.ActorId))
            return Result.Failure<SalesOrderDeliveryState>(Error.Validation(
                "SLS_DELIVERY_REQUEST_INVALID", "수주·출하주문·출하품목 ID와 실행자를 확인하세요."));

        var salesOrderId = command.SalesOrderId!.Trim();
        var deliveryOrderId = command.DeliveryOrderId!.Trim();
        var outcome = await store.TryRequestAsync(
            salesOrderId, deliveryOrderId, command.DeliveryItemId!.Trim(), command.ActorId!.Trim(), ct);
        return outcome switch
        {
            SalesOrderDeliveryOutcome.Requested => Result.Success(
                new SalesOrderDeliveryState(salesOrderId, deliveryOrderId, "Confirmed")),
            SalesOrderDeliveryOutcome.SalesOrderNotFound => Result.Failure<SalesOrderDeliveryState>(Error.NotFound(
                "SLS_ORDER_NOT_FOUND", "수주를 찾을 수 없습니다.")),
            SalesOrderDeliveryOutcome.NotRequestable => Result.Failure<SalesOrderDeliveryState>(Error.Conflict(
                "SLS_DELIVERY_NOT_REQUESTABLE", "미보류·미인계 Draft/Confirmed 수주만 출하를 요청할 수 있습니다.")),
            SalesOrderDeliveryOutcome.InvalidReference => Result.Failure<SalesOrderDeliveryState>(Error.Validation(
                "SLS_DELIVERY_REFERENCE_INVALID", "활성 고객·유효한 품목·공장·납기·수량이 필요합니다.")),
            SalesOrderDeliveryOutcome.DeliveryIdentityConflict => Result.Failure<SalesOrderDeliveryState>(Error.Conflict(
                "SLS_DELIVERY_ID_CONFLICT", "출하주문 또는 품목 ID가 이미 사용 중입니다.")),
            _ => throw new InvalidOperationException("Unknown sales-order delivery outcome."),
        };
    }

    private static bool ValidId(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 50;
}
