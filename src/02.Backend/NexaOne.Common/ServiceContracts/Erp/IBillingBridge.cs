using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>An enrolled MDM customer that billing documents reference as their contact.
/// Name and Active reflect the current master row at read time; Id and Version are the stable enrollment.</summary>
public sealed record BillingContact(Guid Id, Guid Version, string CustomerId, string Name, bool Active);

/// <summary>Immutable invoice snapshot exposed by an authenticated or public PDF download.</summary>
public sealed record BillingDocumentView(BillingDocument Document, BillingContact Contact);

/// <summary>Revocable, expiring public access to one immutable billing-document snapshot.</summary>
public sealed record BillingShareLink(Guid Id, Guid Version, Guid OperationId, Guid DocumentId,
    Guid DocumentVersion, DateTimeOffset ExpiresAt, string CreatedBy, DateTimeOffset CreatedAt,
    DateTimeOffset? RevokedAt = null, long AccessCount = 0, DateTimeOffset? LastAccessedAt = null);

/// <summary>A newly created link. The token is returned only on the first successful operation.</summary>
public sealed record BillingShareSecret(BillingShareLink Link, string? Token);

/// <summary>One privacy-bounded public access event.</summary>
public sealed record BillingShareAccess(Guid Id, Guid ShareId, DateTimeOffset AccessedAt,
    string? ClientAddressHash, string? UserAgent);

/// <summary>Public snapshot resolved from a valid token after its access event is committed.</summary>
public sealed record PublicBillingDocument(BillingDocumentView View, BillingShareLink Link);

/// <summary>An email request tied to a public billing link and the generic delivery queue.</summary>
public sealed record BillingShareDelivery(Guid ShareId, DeliveryRequest Delivery);

/// <summary>Lifecycle of an explicitly registered stock-issue billing source.</summary>
public enum BillingProductSourceState { Eligible = 0, Invoiced = 1, Reversed = 2 }

/// <summary>Pricing and invoice ownership assigned to one immutable inventory issue.</summary>
public sealed record BillingProductSourceInput(Guid ContactId, string Currency, string Description,
    decimal UnitPrice, bool ApplyTax = true, bool ApplyDiscount = true);

/// <summary>An inventory issue explicitly connected to automatic product billing.</summary>
public sealed record BillingProductSource(Guid MovementId, Guid ProductId, Guid VariantId, Guid ContactId,
    DateOnly OccurredOn, string Currency, string Description, decimal UnitPrice, decimal Quantity,
    bool ApplyTax, bool ApplyDiscount, BillingProductSourceState State, Guid? InvoiceId = null);

public enum BillingTimeSourceState { Eligible = 0, Invoiced = 1 }

public sealed record BillingTimeSourceInput(Guid ContactId, string Currency, string Description,
    decimal HourlyRate, bool ApplyTax = true, bool ApplyDiscount = true);

public sealed record BillingTimeSource(Guid TimeEntryId, Guid EmployeeId, Guid? ProjectId, Guid? TaskId,
    Guid ContactId, DateOnly OccurredOn, string Currency, string Description, decimal HourlyRate,
    decimal Hours, bool ApplyTax, bool ApplyDiscount, BillingTimeSourceState State,
    Guid? InvoiceId = null);

/// <summary>Scoped estimates, invoices, credit notes and payments over the Framework billing service. Every operation checks
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
    /// <summary>Creates a draft credit note linked to one issued invoice. Requires billing.credit.</summary>
    Task<BillingDocument> CreateCreditNoteAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid invoiceId, BillingDocumentInput input, CancellationToken ct = default);
    /// <summary>Generates one draft invoice from currently unclaimed provider sources. MES supplies real
    /// uninvoiced expenses plus explicitly registered approved time and inventory occurrences.</summary>
    Task<AutomaticBillingResult> GenerateAutomaticInvoiceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, AutomaticBillingRequest request, CancellationToken ct = default);
    /// <summary>Registers one unreversed inventory issue as a priced product occurrence. Repeating the same
    /// movement and input is idempotent; changed input conflicts. Requires billing.write.</summary>
    Task<BillingProductSource> RegisterProductSourceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid movementId, BillingProductSourceInput input, CancellationToken ct = default);
    /// <summary>Reads the current invoice/reversal state of a registered inventory issue.</summary>
    Task<BillingProductSource> GetProductSourceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid movementId, CancellationToken ct = default);
    /// <summary>Registers one approved HR time entry with invoice ownership and pricing.</summary>
    Task<BillingTimeSource> RegisterTimeSourceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid timeEntryId, BillingTimeSourceInput input, CancellationToken ct = default);
    Task<BillingTimeSource> GetTimeSourceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid timeEntryId, CancellationToken ct = default);
    Task<BillingDocument> UpdateDocumentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, BillingDocumentInput input, CancellationToken ct = default);
    Task<BillingDocument> UpdateCreditNoteAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, BillingDocumentInput input, CancellationToken ct = default);
    Task<BillingDocument> IssueCreditNoteAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<BillingDocument> VoidCreditNoteAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
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
    /// <summary>Reads the current document and enrolled customer for an authenticated PDF download.</summary>
    Task<BillingDocumentView> GetDocumentViewAsync(string userId, Guid tenantId, Guid organizationId,
        Guid documentId, CancellationToken ct = default);
    /// <summary>Creates an immutable, expiring public snapshot. Requires billing.deliver. An exact replay
    /// returns the prior link without its one-time token.</summary>
    Task<BillingShareSecret> CreateShareAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid documentId, DateTimeOffset expiresAt, CancellationToken ct = default);
    Task<BusinessPage<BillingShareLink>> ListSharesAsync(string userId, Guid tenantId, Guid organizationId,
        Guid documentId, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<BillingShareLink> RevokeShareAsync(string userId, Guid tenantId, Guid organizationId,
        Guid shareId, Guid version, CancellationToken ct = default);
    Task<BusinessPage<BillingShareAccess>> ListShareAccessAsync(string userId, Guid tenantId,
        Guid organizationId, Guid shareId, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<BusinessPage<BillingShareDelivery>> ListShareDeliveriesAsync(string userId, Guid tenantId,
        Guid organizationId, Guid shareId, int offset = 0, int limit = 50, CancellationToken ct = default);
    /// <summary>Queues a plain-text email whose template may use documentNumber, documentKind,
    /// customerName, publicUrl and expiresAt. Requires billing.deliver and delivery.queue.</summary>
    Task<BillingShareDelivery> QueueShareDeliveryAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid shareId, string token, Guid templateId, Guid profileId, string recipient,
        string publicUrl, DateTimeOffset? scheduledAt = null, CancellationToken ct = default);
    /// <summary>Resolves an anonymous token and commits an access event before returning the frozen snapshot.</summary>
    Task<PublicBillingDocument> OpenPublicShareAsync(string token, string? clientAddress,
        string? userAgent, CancellationToken ct = default);
}
