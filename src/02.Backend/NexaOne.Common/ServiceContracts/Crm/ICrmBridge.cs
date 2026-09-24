using NexaFramework.Service.Crm;
using NexaFramework.Service.Projects;

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

    Task<WorkPage<Project>> ListProjectsAsync(string userId, Guid tenantId, Guid organizationId,
        ProjectQuery query, CancellationToken ct = default);
    Task<Project> GetProjectAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<Project> CreateProjectAsync(string userId, Guid tenantId, Guid organizationId,
        ProjectInput input, ProjectLinks links, CancellationToken ct = default);
    Task<Project> UpdateProjectAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ProjectInput input, CancellationToken ct = default);
    Task<Project> SetProjectLinksAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ProjectLinks links, CancellationToken ct = default);
    Task DeleteProjectAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);

    Task<WorkPage<Team>> ListTeamsAsync(string userId, Guid tenantId, Guid organizationId,
        TeamQuery query, CancellationToken ct = default);
    Task<Team> GetTeamAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<Team> CreateTeamAsync(string userId, Guid tenantId, Guid organizationId,
        TeamInput input, IReadOnlyList<ProjectMember> members, CancellationToken ct = default);
    Task<Team> UpdateTeamAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, TeamInput input, IReadOnlyList<ProjectMember>? members,
        CancellationToken ct = default);
    Task DeleteTeamAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
}
