using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Collaboration;

/// <summary>Authenticated product boundary for durable outbound delivery administration and queueing.</summary>
public interface IDeliveryBridge : INexaModuleBridge
{
    Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<DeliveryTemplate> CreateTemplateAsync(string userId, Guid tenantId, Guid organizationId,
        string name, string subject, string body, IReadOnlyList<string>? variables = null,
        CancellationToken ct = default);
    Task<DeliveryTemplate> DeactivateTemplateAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<DeliveryProfile> CreateProfileAsync(string userId, Guid tenantId, Guid organizationId,
        string name, string providerKey, string credentialReference, DeliveryRetryPolicy retryPolicy,
        CancellationToken ct = default);
    Task<DeliveryProfile> DeactivateProfileAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<DeliveryRequest> QueueAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, DeliveryQueueInput input, CancellationToken ct = default);
    Task<DeliveryRequest> GetAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<DeliveryRequest> CancelAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<BusinessPage<DeliveryRequest>> ListDeadLettersAsync(string userId, Guid tenantId, Guid organizationId,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<DeliveryRequest> RetryDeadLetterAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid id, Guid version, CancellationToken ct = default);
    Task<DeliveryRequest> DiscardDeadLetterAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid id, Guid version, CancellationToken ct = default);
}
