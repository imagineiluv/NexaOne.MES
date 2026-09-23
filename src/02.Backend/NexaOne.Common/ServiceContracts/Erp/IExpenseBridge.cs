using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

/// <summary>Organization-scoped expense directories, expenses, reimbursement and invoice linkage.
/// Every operation checks current SYS membership and its explicit expense grant.</summary>
public interface IExpenseBridge : INexaModuleBridge
{
    Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId,
        int offset = 0, int limit = 50, CancellationToken ct = default);

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
    Task<ExpenseRecord> CancelExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, string? reason = null, CancellationToken ct = default);

    Task<ExpenseInvoiceLink> LinkInvoiceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid expenseId, Guid expenseVersion, Guid invoiceId, Guid invoiceVersion,
        string? description = null, CancellationToken ct = default);
    Task<ExpenseInvoiceLink> UnlinkInvoiceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid expenseId, Guid expenseVersion, Guid invoiceId, Guid invoiceVersion,
        CancellationToken ct = default);
}
