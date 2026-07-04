using NexaOne.Common;
using NexaOne.SHP.Domain;

namespace NexaOne.SHP.Application.Shp;

public sealed class ShpService
{
    private readonly IDeliveryOrderRepository _orderRepository;
    private readonly IDeliveryItemRepository _itemRepository;
    private readonly IShipmentHistoryRepository _historyRepository;

    public ShpService(
        IDeliveryOrderRepository orderRepository,
        IDeliveryItemRepository itemRepository,
        IShipmentHistoryRepository historyRepository)
    {
        _orderRepository = orderRepository;
        _itemRepository = itemRepository;
        _historyRepository = historyRepository;
    }

    // ── Delivery Orders ───────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<DeliveryOrder>>> GetByPlantAsync(
        string plantId, DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
        => Result.Success(await _orderRepository.GetByPlantAsync(plantId, from, to, ct));

    public Task<int> GetCountByStatusAsync(DeliveryOrderStatus status, CancellationToken ct = default)
        => _orderRepository.GetCountByStatusAsync(status.ToString(), ct);

    public async Task<Result<DeliveryOrder>> CreateOrderAsync(
        string orderId, string customerName, string plantId, DateTime requestedDate, CancellationToken ct = default)
    {
        var result = DeliveryOrder.Create(orderId, customerName, plantId, requestedDate);
        if (result.IsFailure) return result;
        await _orderRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> ConfirmOrderAsync(string orderId, CancellationToken ct = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFoundOf(nameof(DeliveryOrder), orderId));
        var r = order.Confirm();
        if (r.IsFailure) return r;
        await _orderRepository.UpdateAsync(order, ct);
        return Result.Success();
    }

    public async Task<Result> ShipOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFoundOf(nameof(DeliveryOrder), orderId));
        var r = order.Ship(shippedDate);
        if (r.IsFailure) return r;
        await _orderRepository.UpdateAsync(order, ct);
        return Result.Success();
    }

    public async Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default)
    {
        var order = await _orderRepository.GetByIdAsync(orderId, ct);
        if (order is null) return Result.Failure(Error.NotFoundOf(nameof(DeliveryOrder), orderId));
        var r = order.Cancel();
        if (r.IsFailure) return r;
        await _orderRepository.UpdateAsync(order, ct);
        return Result.Success();
    }

    // ── Delivery Items ────────────────────────────────────────────────────────

    public Task<IReadOnlyList<DeliveryItem>> GetItemsByOrderAsync(string orderId, CancellationToken ct = default)
        => _itemRepository.GetByOrderAsync(orderId, ct);

    public async Task<Result<DeliveryItem>> AddItemAsync(
        string itemId, string orderId, string productId, decimal plannedQty,
        string? lotId = null, CancellationToken ct = default)
    {
        var result = DeliveryItem.Create(itemId, orderId, productId, plannedQty, lotId);
        if (result.IsFailure) return result;
        await _itemRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> SetItemActualQtyAsync(string itemId, decimal qty, CancellationToken ct = default)
    {
        var item = await _itemRepository.GetByIdAsync(itemId, ct);
        if (item is null) return Result.Failure(Error.NotFoundOf(nameof(DeliveryItem), itemId));
        var r = item.SetActualQty(qty);
        if (r.IsFailure) return r;
        await _itemRepository.UpdateAsync(item, ct);
        return Result.Success();
    }

    // ── Shipment History ──────────────────────────────────────────────────────

    public Task<IReadOnlyList<ShipmentHistory>> GetShipmentHistoryAsync(string orderId, CancellationToken ct = default)
        => _historyRepository.GetByOrderAsync(orderId, ct);

    public async Task<Result<ShipmentHistory>> RecordShipmentAsync(
        string historyId, string orderId, decimal shippedQty, string shippedBy,
        string? carrier = null, string? trackingNo = null, CancellationToken ct = default)
    {
        var result = ShipmentHistory.Create(historyId, orderId, DateTime.UtcNow, shippedQty, shippedBy, carrier, trackingNo);
        if (result.IsFailure) return result;
        await _historyRepository.AddAsync(result.Value, ct);
        return result;
    }
}
