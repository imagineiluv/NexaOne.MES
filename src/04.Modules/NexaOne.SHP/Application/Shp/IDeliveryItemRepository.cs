using NexaOne.SHP.Domain;

namespace NexaOne.SHP.Application.Shp;

public interface IDeliveryItemRepository
{
    Task<DeliveryItem?> GetByIdAsync(string itemId, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryItem>> GetByOrderAsync(string orderId, CancellationToken ct = default);
    Task AddAsync(DeliveryItem item, CancellationToken ct = default);
    Task UpdateAsync(DeliveryItem item, CancellationToken ct = default);
}
