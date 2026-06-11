using NexaOne.MDM.Domain;

namespace NexaOne.MDM.Application.Equipments;

public interface IProductRepository
{
    Task<Product?> GetByIdAsync(string productId, CancellationToken ct = default);
    Task<IReadOnlyList<Product>> GetAllAsync(CancellationToken ct = default);
    Task AddAsync(Product product, CancellationToken ct = default);
    Task UpdateAsync(Product product, CancellationToken ct = default);
}
