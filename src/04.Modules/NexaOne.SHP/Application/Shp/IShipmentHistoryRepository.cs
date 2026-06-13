using NexaOne.SHP.Domain;

namespace NexaOne.SHP.Application.Shp;

public interface IShipmentHistoryRepository
{
    Task<IReadOnlyList<ShipmentHistory>> GetByOrderAsync(string orderId, CancellationToken ct = default);
    Task AddAsync(ShipmentHistory history, CancellationToken ct = default);
}
