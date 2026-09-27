using NexaOne.Common;

namespace NexaOne.ServiceContracts.Sls;

/// <summary>SLS 수주에서 SHP 출하주문·품목으로의 원자적 인계를 소유합니다.</summary>
public interface ISalesOrderDeliveryBridge : INexaModuleBridge
{
    /// <summary>미보류 Draft/Confirmed 수주만 새 출하주문에 연결합니다. 중복 요청은 충돌입니다.</summary>
    Task<Result<SalesOrderDeliveryState>> RequestDeliveryAsync(
        SalesOrderDeliveryCommand command, CancellationToken ct = default);
}

public sealed record SalesOrderDeliveryCommand(
    string? SalesOrderId, string? DeliveryOrderId, string? DeliveryItemId, string? ActorId);

public sealed record SalesOrderDeliveryState(string SalesOrderId, string DeliveryOrderId, string Status);
