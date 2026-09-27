using NexaOne.Common;

namespace NexaOne.ServiceContracts.Sls;

/// <summary>수주 초안과 단순 상태 전이의 유일한 쓰기 인터페이스입니다. 출하 인계는 별도 계약을 사용합니다.</summary>
public interface ISalesOrderCommandBridge : INexaModuleBridge
{
    /// <summary>새 수주를 Draft로 만들거나 미보류 Draft를 편집합니다. MDM 참조와 수량·납기를 함께 검증합니다.</summary>
    Task<Result<SalesOrderCommandState>> SaveDraftAsync(
        SalesOrderDraftCommand command, CancellationToken ct = default);

    /// <summary>미보류·미연결 Draft만 삭제합니다. 연결 판매요청이 있으면 충돌입니다.</summary>
    Task<Result> DeleteDraftAsync(string? salesOrderId, string? actorId, CancellationToken ct = default);

    /// <summary>미보류 Draft를 Confirmed로 전이합니다.</summary>
    Task<Result<SalesOrderCommandState>> ConfirmAsync(
        string? salesOrderId, string? actorId, CancellationToken ct = default);

    /// <summary>미보류 Producing 또는 Delivered를 Closed로 전이합니다.</summary>
    Task<Result<SalesOrderCommandState>> CloseAsync(
        string? salesOrderId, string? actorId, CancellationToken ct = default);
}

public sealed record SalesOrderDraftCommand(
    string? SalesOrderId,
    string? SalesOrderName,
    string? PlantId,
    string? CustomerId,
    string? ProductId,
    DateTime? PlanStartDate,
    DateTime? PlanEndDate,
    decimal PlanQty,
    string? ActorId);

public sealed record SalesOrderCommandState(string SalesOrderId, string Status);
