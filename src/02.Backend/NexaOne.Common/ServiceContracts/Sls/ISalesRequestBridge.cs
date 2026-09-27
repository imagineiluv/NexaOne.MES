using NexaOne.Common;

namespace NexaOne.ServiceContracts.Sls;

/// <summary>판매요청 초안과 수주 초안으로의 원자적 수령을 SLS 모듈이 소유합니다.</summary>
public interface ISalesRequestBridge : INexaModuleBridge
{
    /// <summary>새 ID만 Draft로 생성합니다. 동일 ID 재호출은 내용이 같아도 충돌입니다.</summary>
    Task<Result<SalesRequestState>> CreateDraftAsync(
        SalesRequestDraftCommand command, CancellationToken ct = default);

    /// <summary>Draft 요청을 새 수주 Draft에 원자적으로 연결합니다. 재수령·수주 ID 중복은 충돌이며 상태를 바꾸지 않습니다.</summary>
    Task<Result<SalesRequestState>> ReceiveAsync(
        SalesRequestReceiptCommand command, CancellationToken ct = default);
}

public sealed record SalesRequestDraftCommand(
    string? SalesRequestId,
    string? SalesRequestName,
    string? CustomerId,
    string? ProductId,
    DateTime RequestDate,
    decimal RequestQty,
    string? ActorId);

public sealed record SalesRequestReceiptCommand(
    string? SalesRequestId,
    string? SalesOrderId,
    string? PlantId,
    string? SalesOrderName,
    DateTime PlanStartDate,
    DateTime PlanEndDate,
    string? ActorId);

public sealed record SalesRequestState(string SalesRequestId, string Status, string? SalesOrderId);
