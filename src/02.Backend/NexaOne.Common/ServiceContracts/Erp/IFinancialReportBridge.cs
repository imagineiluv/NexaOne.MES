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

    /// <summary>Builds a currency-separated cash-inflow report by UTC payment received date.</summary>
    Task<CashFlowReport> BuildCashFlowAsync(string userId, Guid tenantId, Guid organizationId,
        CashFlowReportPeriod period, CancellationToken ct = default);

    /// <summary>Builds the same payment-date snapshot and exports it as deterministic CSV.</summary>
    Task<string> ExportCashFlowCsvAsync(string userId, Guid tenantId, Guid organizationId,
        CashFlowReportPeriod period, CancellationToken ct = default);

    /// <summary>Builds and persistently captures one immutable report. The operation ID is the snapshot ID.</summary>
    Task<FinancialReportSnapshot> CreateSnapshotAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, FinancialReportSnapshotKind kind, DateOnly start, DateOnly end,
        CancellationToken ct = default);

    /// <summary>Gets one persisted report payload in its organization scope.</summary>
    Task<FinancialReportSnapshot> GetSnapshotAsync(string userId, Guid tenantId, Guid organizationId,
        Guid snapshotId, CancellationToken ct = default);

    /// <summary>Lists persisted report metadata newest first.</summary>
    Task<BusinessPage<FinancialReportSnapshotSummary>> ListSnapshotsAsync(string userId, Guid tenantId,
        Guid organizationId, FinancialReportSnapshotKind? kind = null, int offset = 0, int limit = 50,
        CancellationToken ct = default);

    /// <summary>Rebuilds the original period and records whether its business content still matches.</summary>
    Task<FinancialReportSnapshotComparison> RegenerateSnapshotAsync(string userId, Guid tenantId,
        Guid organizationId, Guid snapshotId, CancellationToken ct = default);

    /// <summary>Returns the originally persisted CSV and records the download.</summary>
    Task<FinancialReportSnapshotDownload> DownloadSnapshotAsync(string userId, Guid tenantId,
        Guid organizationId, Guid snapshotId, CancellationToken ct = default);

    /// <summary>Lists append-only creation, regeneration and download history newest first.</summary>
    Task<BusinessPage<FinancialReportSnapshotAuditEntry>> ListSnapshotAuditAsync(string userId,
        Guid tenantId, Guid organizationId, Guid snapshotId, int offset = 0, int limit = 50,
        CancellationToken ct = default);
}
