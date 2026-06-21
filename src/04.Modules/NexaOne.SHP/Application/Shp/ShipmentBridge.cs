using NexaOne.Common;
using NexaOne.ServiceContracts.Shp;
using NexaOne.SHP.Domain;

namespace NexaOne.SHP.Application.Shp;

/// <summary>ADR-008 얇은 브리지 어댑터 — ShpService에 위임하고 DeliveryOrder를 계약 DTO로 매핑(Status enum→string).
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IShipmentBridge로 캐스트해 DI에 등록한다.</summary>
public sealed class ShipmentBridge : IShipmentBridge
{
    private readonly ShpService _service;
    public ShipmentBridge(ShpService service) => _service = service;

    public async Task<IReadOnlyList<DeliveryOrderDto>> GetOrdersByPlantAsync(
        string plantId, DateTime? from, DateTime? to, CancellationToken ct = default)
    {
        var r = await _service.GetByPlantAsync(plantId, from, to, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : new List<DeliveryOrderDto>();
    }

    public async Task<Result<DeliveryOrderDto>> CreateOrderAsync(
        string orderId, string customerName, string plantId, DateTime requestedDate, CancellationToken ct = default)
    {
        var r = await _service.CreateOrderAsync(orderId, customerName, plantId, requestedDate, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<DeliveryOrderDto>(r.Error);
    }

    public Task<Result> ConfirmOrderAsync(string orderId, CancellationToken ct = default) => _service.ConfirmOrderAsync(orderId, ct);
    public Task<Result> ShipOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default) => _service.ShipOrderAsync(orderId, shippedDate, ct);
    public Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default) => _service.CancelOrderAsync(orderId, ct);

    private static DeliveryOrderDto ToDto(DeliveryOrder o)
        => new(o.Id, o.CustomerName, o.PlantId, o.RequestedDate, o.ShippedDate, o.Status.ToString(), o.TotalQty);
}
