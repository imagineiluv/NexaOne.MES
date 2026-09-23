using System.Data.Common;
using NexaOne.Common;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>Connects sibling modules to the current SYS membership bridge without owning its transaction.</summary>
public sealed class BusinessMembershipProxy : IBusinessMembershipBridge
{
    private readonly ModuleBeanResolver _resolver;

    public BusinessMembershipProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<BusinessMembership?> GetAccessAsync(
        string authenticatedUserId, Guid tenantId, Guid organizationId, CancellationToken ct = default)
        => Resolve().GetAccessAsync(authenticatedUserId, tenantId, organizationId, ct);

    public Task<BusinessMembership?> GetAccessInTransactionAsync(
        DbTransaction transaction, string authenticatedUserId, Guid tenantId, Guid organizationId,
        CancellationToken ct = default)
        => Resolve().GetAccessInTransactionAsync(transaction, authenticatedUserId, tenantId, organizationId, ct);

    public Task<string> RequireAdministratorInTransactionAsync(
        DbTransaction transaction, string administratorId, CancellationToken ct = default)
        => Resolve().RequireAdministratorInTransactionAsync(transaction, administratorId, ct);

    public Task<BusinessServiceActor> EnsureServiceActorInTransactionAsync(
        DbTransaction transaction, string administratorId, string serviceUserId, string displayName,
        CancellationToken ct = default)
        => Resolve().EnsureServiceActorInTransactionAsync(
            transaction, administratorId, serviceUserId, displayName, ct);

    public Task<IReadOnlyList<BusinessMembership>> ListAccessInTransactionAsync(
        DbTransaction transaction, string authenticatedUserId, Guid? afterTenantId = null,
        Guid? afterOrganizationId = null, int limit = 128, CancellationToken ct = default)
        => Resolve().ListAccessInTransactionAsync(transaction, authenticatedUserId, afterTenantId, afterOrganizationId, limit, ct);

    public Task<BusinessMembership?> GetActiveMemberInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid businessUserId,
        CancellationToken ct = default)
        => Resolve().GetActiveMemberInTransactionAsync(transaction, tenantId, organizationId, businessUserId, ct);

    public Task<IReadOnlyList<BusinessMembership>> ListActiveMembersInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid? afterBusinessUserId = null,
        int limit = 128, CancellationToken ct = default)
        => Resolve().ListActiveMembersInTransactionAsync(transaction, tenantId, organizationId,
            afterBusinessUserId, limit, ct);

    public Task<Result<BusinessMembership>> GetMembershipAsync(
        string administratorId, Guid tenantId, Guid organizationId, string userId,
        CancellationToken ct = default)
        => Resolve().GetMembershipAsync(administratorId, tenantId, organizationId, userId, ct);

    public Task<Result<BusinessMembership>> SaveMembershipAsync(
        string administratorId, Guid tenantId, Guid organizationId, string userId,
        BusinessMembershipChange change, CancellationToken ct = default)
        => Resolve().SaveMembershipAsync(administratorId, tenantId, organizationId, userId, change, ct);

    private IBusinessMembershipBridge Resolve() =>
        _resolver.Resolve<IBusinessMembershipBridge>("Sys", "businessMembershipBridge");
}
