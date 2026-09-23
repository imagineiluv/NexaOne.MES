using System.Data.Common;
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

    /// <summary>
    /// Reads current access using the caller's live Serializable transaction. Returns null for
    /// invalid keys, missing/revoked membership or an inactive/deleted user. The caller retains
    /// ownership of the transaction and its connection.
    /// </summary>
    Task<BusinessMembership?> GetAccessInTransactionAsync(
        DbTransaction transaction, string authenticatedUserId, Guid tenantId, Guid organizationId,
        CancellationToken ct = default);

    /// <summary>
    /// Lists only the authenticated user's active memberships while the SYS user is active and not deleted.
    /// Uses the caller's live Serializable transaction, with no writes, commit or disposal. Results are
    /// detached and ordered by stored canonical tenant ID then organization ID. Both exclusive cursors
    /// must be null or nonempty together; limit is 1–128. Invalid arguments throw ArgumentException.
    /// Missing/inactive/deleted users return an empty list; grants are read afresh on every call.
    /// </summary>
    Task<IReadOnlyList<BusinessMembership>> ListAccessInTransactionAsync(
        DbTransaction transaction, string authenticatedUserId, Guid? afterTenantId = null,
        Guid? afterOrganizationId = null, int limit = 128, CancellationToken ct = default);

    /// <summary>Returns one active member of the requested scope by its stable business identity.
    /// Uses the caller's live Serializable transaction and returns null for an inactive/deleted user.</summary>
    Task<BusinessMembership?> GetActiveMemberInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid businessUserId,
        CancellationToken ct = default);

    /// <summary>Lists active members of one scope by stable business identity, after an optional exclusive
    /// identity cursor. The caller owns the live Serializable transaction; limit is 1–128.</summary>
    Task<IReadOnlyList<BusinessMembership>> ListActiveMembersInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid? afterBusinessUserId = null,
        int limit = 128, CancellationToken ct = default);

    /// <summary>
    /// Rechecks live SYS sys:manage authority using the caller's Serializable transaction and
    /// returns the stored canonical administrator ID. Does not commit or dispose the transaction.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The administrator is no longer authorized.</exception>
    Task<string> RequireAdministratorInTransactionAsync(
        DbTransaction transaction, string administratorId, CancellationToken ct = default);

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
