using NexaOne.Common;

namespace NexaOne.ServiceContracts.Shp;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — SHP 출하주문 생명주기. plugin(SHP)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. Result로 상태전이 분기(Conflict/Validation/Success)를 손실 없이 전달한다.</summary>
public interface IShipmentBridge
{
    Task<IReadOnlyList<DeliveryOrderDto>> GetOrdersByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<Result<DeliveryOrderDto>> CreateOrderAsync(string orderId, string customerName, string plantId, DateTime requestedDate, CancellationToken ct = default);
    Task<Result> ConfirmOrderAsync(string orderId, CancellationToken ct = default);
    Task<Result> ShipOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default);
    Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default);
}
