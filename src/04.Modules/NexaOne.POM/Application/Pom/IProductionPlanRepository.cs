using NexaOne.POM.Domain;

namespace NexaOne.POM.Application.Pom;

public interface IProductionPlanRepository
{
    Task<ProductionPlan?> GetByIdAsync(string planId, CancellationToken ct = default);
    Task<IReadOnlyList<ProductionPlan>> GetByPlantAsync(string plantId, DateTime? from, DateTime? to, CancellationToken ct = default);
    Task<int> GetCountByStatusAsync(string status, CancellationToken ct = default);
    Task AddAsync(ProductionPlan plan, CancellationToken ct = default);
    Task UpdateAsync(ProductionPlan plan, CancellationToken ct = default);
}
