using System.Data.Common;

namespace NexaOne.ServiceContracts.Shp;

/// <summary>SHP 출하 확정 사실을 호출자 소유 트랜잭션에서 읽는 소유자 계약입니다.</summary>
public interface ISalesOrderShipmentEvidence : INexaModuleBridge
{
    Task<SalesOrderShipmentSnapshot?> FindAsync(
        DbTransaction transaction, string deliveryOrderId, CancellationToken ct = default);
}

public sealed record SalesOrderShipmentSnapshot(
    string DeliveryOrderId,
    string PlantId,
    string Status,
    DateTime? ShippedDate,
    string? ProductId,
    decimal? PlannedQty,
    decimal? ActualQty,
    int ItemCount,
    decimal? RecordedShippedQty,
    int ShipmentHistoryCount);
