using NexaOne.Common;

namespace NexaOne.ServiceContracts.Sys;

/// <summary>
/// SYS-owned, persisted membership for business services. The caller supplies an authenticated
/// MES user, never a body actor or an asserted plant/organization claim. Results are snapshots;
/// consumers must resolve again for each operation and serialize sensitive writes with revocation.
/// A membership grants the listed operations throughout its tenant/organization, not just own rows.
/// </summary>
public interface IBusinessMembershipBridge : INexaModuleBridge
{
    /// <summary>Returns null for a missing/revoked membership or inactive/deleted user.</summary>
    Task<BusinessMembership?> GetAccessAsync(
        string authenticatedUserId, Guid tenantId, Guid organizationId, CancellationToken ct = default);

    /// <summary>Reads even revoked memberships. Rechecks live SYS sys:manage authority.</summary>
    /// <exception cref="UnauthorizedAccessException">The administrator is no longer authorized.</exception>
    Task<Result<BusinessMembership>> GetMembershipAsync(
        string administratorId, Guid tenantId, Guid organizationId, string userId,
        CancellationToken ct = default);

    /// <summary>
    /// ExpectedVersion=0 creates; a positive version replaces exactly that revision. Every successful
    /// write appends an audit revision in the same transaction. Stale retries conflict: read before retrying.
    /// The business user GUID is generated once by SYS and cannot be supplied or changed by the caller.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The administrator is no longer authorized.</exception>
    Task<Result<BusinessMembership>> SaveMembershipAsync(
        string administratorId, Guid tenantId, Guid organizationId, string userId,
        BusinessMembershipChange change, CancellationToken ct = default);
}

public sealed record BusinessMembership(
    Guid TenantId, Guid OrganizationId, string UserId, Guid BusinessUserId,
    bool IsActive, long Version, IReadOnlyList<string> Permissions);

public sealed record BusinessMembershipChange(
    long ExpectedVersion, bool IsActive, IReadOnlyList<string> Permissions);
