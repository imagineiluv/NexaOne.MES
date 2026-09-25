using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>Active organization member exposed as a stable expense employee choice.</summary>
public sealed record ExpenseEmployee(Guid Id, string UserId);

/// <summary>Editable values of one organization-scoped expense tag.</summary>
public sealed record ExpenseTagInput(string Name);

/// <summary>Versioned expense tag retained while historical records reference it.</summary>
public sealed record ExpenseTag(Guid Id, BusinessScope Scope, Guid Version, ExpenseTagInput Input,
    bool Active = true);

/// <summary>Filtered tag directory query with exact-total paging.</summary>
public sealed record ExpenseTagQuery(string? Text = null, bool IncludeInactive = false,
    int Offset = 0, int Limit = 50);

public sealed record ExpenseReceipt(Guid Id, Guid ExpenseId, BusinessScope Scope, Guid Version,
    string FileName, string ContentType, long Size, string Sha256, string UploadedBy,
    DateTimeOffset UploadedAt);

public sealed record ExpenseReceiptDownload(ExpenseReceipt Receipt, byte[] Content);

/// <summary>Organization-scoped expense directories, expenses, reimbursement and invoice linkage.
/// Every operation checks current SYS membership and its explicit expense grant.</summary>
public interface IExpenseBridge : INexaModuleBridge
{
    Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<BusinessPage<ExpenseEmployee>> ListEmployeesAsync(string userId, Guid tenantId, Guid organizationId,
        int offset = 0, int limit = 50, CancellationToken ct = default);

    Task<ExpenseTag> CreateTagAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseTagInput input, CancellationToken ct = default);
    Task<ExpenseTag> GetTagAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<ExpenseTag>> ListTagsAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseTagQuery? query = null, CancellationToken ct = default);
    Task<ExpenseTag> UpdateTagAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseTagInput input, CancellationToken ct = default);
    Task<ExpenseTag> SetTagActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default);

    Task<ExpenseCategory> CreateCategoryAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseCategoryInput input, CancellationToken ct = default);
    Task<ExpenseCategory> GetCategoryAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<ExpenseCategory>> ListCategoriesAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseDirectoryQuery? query = null, CancellationToken ct = default);
    Task<ExpenseCategory> UpdateCategoryAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseCategoryInput input, CancellationToken ct = default);
    Task<ExpenseCategory> SetCategoryActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default);

    Task<ExpenseVendor> CreateVendorAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseVendorInput input, CancellationToken ct = default);
    Task<ExpenseVendor> GetVendorAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<ExpenseVendor>> ListVendorsAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseDirectoryQuery? query = null, CancellationToken ct = default);
    Task<ExpenseVendor> UpdateVendorAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseVendorInput input, CancellationToken ct = default);
    Task<ExpenseVendor> SetVendorActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default);

    Task<ExpenseRecord> CreateExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, ExpenseInput input, CancellationToken ct = default);
    Task<ExpenseRecord> UpdateExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseInput input, CancellationToken ct = default);
    Task<ExpenseRecord> GetExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<ExpenseRecord>> ListExpensesAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseQuery? query = null, CancellationToken ct = default);
    Task<ExpenseRecord> MarkInvoicedAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<ExpenseRecord> MarkPaidAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<ExpenseRecord> ReimburseExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid id, Guid version, DateTimeOffset paidAt, string? reference = null,
        CancellationToken ct = default);
    Task<ExpensePayoutRequest> QueuePayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, ExpensePayoutInput input, CancellationToken ct = default);
    Task<ExpensePayoutRequest> GetPayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, CancellationToken ct = default);
    Task<BusinessPage<ExpensePayoutRequest>> ListPayoutsAsync(string userId, Guid tenantId,
        Guid organizationId, Guid? expenseId = null, int offset = 0, int limit = 50,
        CancellationToken ct = default);
    Task<BusinessPage<ExpensePayoutRequest>> ListFailedPayoutsAsync(string userId, Guid tenantId,
        Guid organizationId, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<ExpensePayoutRequest> CancelPayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, Guid version, CancellationToken ct = default);
    Task<ExpensePayoutRequest> RetryFailedPayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid payoutId, Guid version, CancellationToken ct = default);
    Task<ExpensePayoutRequest> DiscardFailedPayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid payoutId, Guid version, CancellationToken ct = default);
    Task<ExpenseRecord> CancelExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, string? reason = null, CancellationToken ct = default);
    Task<ExpenseReceipt?> GetReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, CancellationToken ct = default);
    Task<ExpenseReceiptDownload> DownloadReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, CancellationToken ct = default);
    Task<ExpenseReceipt> PutReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, Guid? expectedVersion, string fileName, string contentType, byte[] content,
        CancellationToken ct = default);
    Task DeleteReceiptAsync(string userId, Guid tenantId, Guid organizationId,
        Guid expenseId, Guid version, CancellationToken ct = default);

    Task<ExpenseInvoiceLink> LinkInvoiceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid expenseId, Guid expenseVersion, Guid invoiceId, Guid invoiceVersion,
        string? description = null, CancellationToken ct = default);
    Task<ExpenseInvoiceLink> UnlinkInvoiceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid expenseId, Guid expenseVersion, Guid invoiceId, Guid invoiceVersion,
        CancellationToken ct = default);
}
