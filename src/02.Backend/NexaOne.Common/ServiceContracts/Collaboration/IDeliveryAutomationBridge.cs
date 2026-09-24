using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.Common;

namespace NexaOne.ServiceContracts.Collaboration;

/// <summary>
/// Host-only authority and lifecycle boundary for unattended outbound delivery. Every worker operation rechecks
/// the persisted principal and exact scope version before it may lease or settle a request.
/// </summary>
public interface IDeliveryAutomationBridge : INexaModuleBridge
{
    Task<Result<DeliveryServicePrincipal>> GetPrincipalAsync(
        string administratorId, string principalId, CancellationToken ct = default);
    Task<Result<DeliveryServicePrincipal>> SavePrincipalAsync(
        string administratorId, string principalId, DeliveryServicePrincipalChange change,
        CancellationToken ct = default);
    Task<Result<DeliveryServicePrincipalScope>> GetScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        CancellationToken ct = default);
    Task<Result<DeliveryServicePrincipalScope>> SaveScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        DeliveryServicePrincipalScopeChange change, CancellationToken ct = default);

    Task<BusinessPage<DeliveryServicePrincipalScope>> ListActiveScopesAsync(
        string principalId, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<IReadOnlyList<DeliveryRequest>> ClaimDueAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion,
        int limit, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<DeliveryRequest> CompleteAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion,
        Guid deliveryId, Guid version, Guid leaseId, string providerReceipt,
        CancellationToken ct = default);
    Task<DeliveryRequest> FailAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion,
        Guid deliveryId, Guid version, Guid leaseId, string errorCode,
        CancellationToken ct = default);
}

/// <summary>A durable non-interactive identity dedicated to outbound delivery dispatch.</summary>
public sealed record DeliveryServicePrincipal(
    string Id, Guid AuditActorId, string Name, bool IsActive, long Version);

public sealed record DeliveryServicePrincipalChange(long ExpectedVersion, string Name, bool IsActive);

/// <summary>An explicit delivery.dispatch grant for one tenant and organization.</summary>
public sealed record DeliveryServicePrincipalScope(
    string PrincipalId, Guid TenantId, Guid OrganizationId, bool IsActive, long Version);

public sealed record DeliveryServicePrincipalScopeChange(long ExpectedVersion, bool IsActive);
