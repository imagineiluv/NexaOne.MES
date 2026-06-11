using NexaOne.DLV.Domain;

namespace NexaOne.DLV.Application.Dlv;

public interface IShipmentHistoryRepository
{
    Task<IReadOnlyList<ShipmentHistory>> GetByOrderAsync(string orderId, CancellationToken ct = default);
    Task AddAsync(ShipmentHistory history, CancellationToken ct = default);
}
