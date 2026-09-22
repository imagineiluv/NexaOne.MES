using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>An enrolled MDM customer that billing documents reference as their contact.
/// Name and Active reflect the current master row at read time; Id and Version are the stable enrollment.</summary>
public sealed record BillingContact(Guid Id, Guid Version, string CustomerId, string Name, bool Active);

/// <summary>Scoped estimates, invoices and payments over the Framework billing service. Every operation checks
/// current SYS membership and grants in its owning Serializable transaction; no plant binding is involved.</summary>
public interface IBillingBridge : INexaModuleBridge
{
    /// <summary>Lists the caller's active memberships that carry at least one billing grant, in tenant then
    /// organization order, with the matching total. No plant binding is consulted and no identity is created.</summary>
    Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    /// <summary>Enrolls an active MDM customer as a billing contact. Requires billing.write.
    /// Repeating the same customer returns the existing contact.</summary>
    Task<BillingContact> EnrollContactAsync(string userId, Guid tenantId, Guid organizationId,
        string customerId, CancellationToken ct = default);
    /// <summary>Lists enrolled contacts in customer-ID order with current master names and activity.</summary>
    Task<BusinessPage<BillingContact>> ListContactsAsync(string userId, Guid tenantId, Guid organizationId,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    /// <summary>Uses the Framework's scope-wide operation replay contract; current permission is required on every retry.</summary>
    Task<BillingDocument> CreateDocumentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, BillingKind kind, BillingDocumentInput input, CancellationToken ct = default);
    Task<BillingDocument> UpdateDocumentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, BillingDocumentInput input, CancellationToken ct = default);
    Task<BillingDocument> GetDocumentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<BillingDocument>> ListDocumentsAsync(string userId, Guid tenantId, Guid organizationId,
        BillingKind? kind = null, BillingStatus? status = null, Guid? contactId = null,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<BillingDocument> MarkSentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<BillingDocument> DecideEstimateAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool accepted, CancellationToken ct = default);
    Task<BillingDocument> ConvertEstimateAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid estimateId, Guid version, CancellationToken ct = default);
    Task<BillingDocument> VoidDocumentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<PaymentRecord> RecordPaymentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, PaymentInput input, CancellationToken ct = default);
    Task<PaymentRecord> CancelPaymentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, string? reason, CancellationToken ct = default);
    Task<PaymentRecord> GetPaymentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<PaymentRecord>> ListPaymentsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid documentId, PaymentState? state = null, int offset = 0, int limit = 50, CancellationToken ct = default);
}
