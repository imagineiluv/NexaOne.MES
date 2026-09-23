using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Common;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>
/// Host-only authority and execution boundary for unattended recurring ERP work. A service principal is not a
/// login identity; every scope read and occurrence execution rechecks its persisted active grant.
/// </summary>
public interface IRecurringAutomationBridge : INexaModuleBridge
{
    Task<Result<RecurringServicePrincipal>> GetPrincipalAsync(
        string administratorId, string principalId, CancellationToken ct = default);
    Task<Result<RecurringServicePrincipal>> SavePrincipalAsync(
        string administratorId, string principalId, RecurringServicePrincipalChange change,
        CancellationToken ct = default);
    Task<Result<RecurringServicePrincipalScope>> GetScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        CancellationToken ct = default);
    Task<Result<RecurringServicePrincipalScope>> SaveScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        RecurringServicePrincipalScopeChange change, CancellationToken ct = default);

    Task<BusinessPage<RecurringServicePrincipalScope>> ListActiveScopesAsync(
        string principalId, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<BusinessPage<RecurringRule>> ListDueRulesAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion, DateOnly localDate,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<RecurringExecution> ExecuteOccurrenceAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion,
        Guid ruleId, DateOnly month,
        CancellationToken ct = default);
}

/// <summary>A durable non-interactive identity dedicated to recurring ERP execution.</summary>
public sealed record RecurringServicePrincipal(
    string Id, Guid AuditActorId, string Name, bool IsActive, long Version);

public sealed record RecurringServicePrincipalChange(long ExpectedVersion, string Name, bool IsActive);

/// <summary>An explicit recurring.execute grant for one tenant and organization.</summary>
public sealed record RecurringServicePrincipalScope(
    string PrincipalId, Guid TenantId, Guid OrganizationId, bool IsActive, long Version,
    string? TimeZoneId = null, int CatchUpMonths = 0);

public sealed record RecurringServicePrincipalScopeChange(
    long ExpectedVersion, bool IsActive, string? TimeZoneId = null, int CatchUpMonths = 0);
