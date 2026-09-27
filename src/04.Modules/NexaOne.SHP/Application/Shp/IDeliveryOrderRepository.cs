using NexaOne.SHP.Domain;

namespace NexaOne.SHP.Application.Shp;

public interface IDeliveryOrderRepository
{
    Task<DeliveryOrder?> GetByIdAsync(string orderId, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryOrder>> GetByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<int> GetCountByStatusAsync(string status, CancellationToken ct = default);
    Task AddAsync(DeliveryOrder order, CancellationToken ct = default);
    Task<bool> TryUpdateAsync(DeliveryOrder order, DeliveryOrderStatus expectedStatus, bool expectedHeld,
        CancellationToken ct = default);
}
