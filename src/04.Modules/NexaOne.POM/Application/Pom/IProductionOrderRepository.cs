using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.Pom;

public interface IProductionOrderRepository
{
    Task<ProductionOrder?> GetByIdAsync(string orderId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductionOrder>> GetByPlanAsync(string planId, CancellationToken ct = default);
    Task AddAsync(ProductionOrder order, CancellationToken ct = default);
    Task UpdateAsync(ProductionOrder order, CancellationToken ct = default);
}
