using NexaFramework.Service.Crm;

namespace NexaOne.ServiceContracts.Crm;

/// <summary>Authenticated MES boundary for scoped CRM pipelines and row-filtered deals.</summary>
public interface ICrmBridge : INexaModuleBridge
{
    Task<CrmPage<Pipeline>> ListPipelinesAsync(string userId, Guid tenantId, Guid organizationId,
        PipelineQuery query, CancellationToken ct = default);
    Task<PipelineDetails> GetPipelineAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<PipelineDetails> CreatePipelineAsync(string userId, Guid tenantId, Guid organizationId,
        PipelineInput input, CancellationToken ct = default);
    Task<PipelineDetails> UpdatePipelineAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, PipelineInput input, DealRemoval removal, CancellationToken ct = default);
    Task DeletePipelineAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, DealRemoval removal, CancellationToken ct = default);

    Task<CrmPage<Deal>> ListDealsAsync(string userId, Guid tenantId, Guid organizationId,
        DealQuery query, CancellationToken ct = default);
    Task<Deal> GetDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<Deal> CreateDealAsync(string userId, Guid tenantId, Guid organizationId,
        DealInput input, CancellationToken ct = default);
    Task<Deal> UpdateDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, DealInput input, CancellationToken ct = default);
    Task<Deal> MoveDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, Guid stageId, CancellationToken ct = default);
    Task DeleteDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
}
