using NexaOne.DLV.Domain;

namespace NexaOne.DLV.Application.Dlv;

public interface IDeliveryOrderRepository
{
    Task<DeliveryOrder?> GetByIdAsync(string orderId, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryOrder>> GetByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<int> GetCountByStatusAsync(string status, CancellationToken ct = default);
    Task AddAsync(DeliveryOrder order, CancellationToken ct = default);
    Task UpdateAsync(DeliveryOrder order, CancellationToken ct = default);
}
