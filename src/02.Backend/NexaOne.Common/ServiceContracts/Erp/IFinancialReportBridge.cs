using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>Organization financial snapshots built from billing, income and expense rows read atomically.</summary>
public interface IFinancialReportBridge : INexaModuleBridge
{
    /// <summary>Lists active memberships that grant financial-report.read.</summary>
    Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId,
        int offset = 0, int limit = 50, CancellationToken ct = default);

    /// <summary>Builds a currency-separated report for one inclusive business-date period.</summary>
    Task<FinancialReport> BuildAsync(string userId, Guid tenantId, Guid organizationId,
        FinancialReportPeriod period, CancellationToken ct = default);

    /// <summary>Builds the same atomic snapshot and exports it as deterministic CSV.</summary>
    Task<string> ExportCsvAsync(string userId, Guid tenantId, Guid organizationId,
        FinancialReportPeriod period, CancellationToken ct = default);
}
