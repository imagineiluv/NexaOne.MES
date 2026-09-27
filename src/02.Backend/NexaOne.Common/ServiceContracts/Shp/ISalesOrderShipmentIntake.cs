using System.Data.Common;

namespace NexaOne.ServiceContracts.Shp;

/// <summary>SHP가 수주에서 파생된 출하주문과 품목을 호출자 소유 Serializable 트랜잭션에 기록합니다.</summary>
public interface ISalesOrderShipmentIntake : INexaModuleBridge
{
    /// <summary>커밋·롤백은 호출자 소유입니다. 실패 시 예외를 전달해 두 삽입을 함께 롤백합니다.</summary>
    Task CreateDraftAsync(DbTransaction transaction, SalesOrderShipmentDraft draft, CancellationToken ct = default);
}

public sealed record SalesOrderShipmentDraft(
    string DeliveryOrderId,
    string DeliveryItemId,
    string CustomerName,
    string PlantId,
    string ProductId,
    DateTime RequestedDate,
    decimal PlannedQty,
    string ActorId);
