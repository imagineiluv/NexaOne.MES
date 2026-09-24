using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>Catalog and execution boundary for organization-scoped ERP business reports.</summary>
public interface IBusinessReportBridge : INexaModuleBridge
{
    /// <summary>Lists active memberships that grant access to the ERP report catalog.</summary>
    Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId,
        int offset = 0, int limit = 50, CancellationToken ct = default);

    /// <summary>Lists report definitions visible to the actor in one organization.</summary>
    Task<IReadOnlyList<BusinessReportDefinition>> ListAsync(string userId, Guid tenantId,
        Guid organizationId, CancellationToken ct = default);

    /// <summary>Builds one registered report with the requested calendar aggregation.</summary>
    Task<BusinessReport> BuildAsync(string userId, Guid tenantId, Guid organizationId,
        string reportKey, BusinessReportPeriod period, BusinessReportUnit unit,
        CancellationToken ct = default);

    /// <summary>Builds and exports one registered report as deterministic CSV.</summary>
    Task<string> ExportCsvAsync(string userId, Guid tenantId, Guid organizationId,
        string reportKey, BusinessReportPeriod period, BusinessReportUnit unit,
        CancellationToken ct = default);
}
