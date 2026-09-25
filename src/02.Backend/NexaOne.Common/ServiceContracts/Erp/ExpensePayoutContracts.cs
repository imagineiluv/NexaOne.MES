using NexaFramework.Service;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ServiceContracts.Erp;

public enum ExpensePayoutState
{
    Pending,
    Processing,
    Completed,
    Failed,
    Cancelled
}

/// <summary>Immutable provider selection for one employee expense reimbursement.</summary>
public sealed record ExpensePayoutInput(Guid ExpenseId, Guid ExpenseVersion, string ProviderKey);

/// <summary>Durable execution state. Provider credentials and employee destination details are never persisted here.</summary>
public sealed record ExpensePayoutRequest(
    Guid Id,
    Guid Version,
    Guid OperationId,
    BusinessScope Scope,
    Guid ExpenseId,
    Guid ExpenseVersion,
    Guid EmployeeId,
    decimal Amount,
    string Currency,
    string ProviderKey,
    ExpensePayoutState State,
    int AttemptCount,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    Guid? LeaseId = null,
    DateTimeOffset? LeaseExpiresAt = null,
    string? ProviderReference = null,
    DateTimeOffset? PaidAt = null,
    string? ErrorCode = null);

/// <summary>External payout instruction. Providers must converge repeated calls with the same operation ID.</summary>
public sealed record ExpensePayoutInstruction(
    Guid OperationId,
    Guid TenantId,
    Guid OrganizationId,
    Guid EmployeeId,
    decimal Amount,
    string Currency);

public sealed record ExpensePayoutReceipt(string Reference, DateTimeOffset PaidAt);

public interface IExpensePayoutProviderRegistry
{
    IExpensePayoutProvider GetRequired(string providerKey);
}

/// <summary>Host-owned external I/O boundary. Implementations must not log credentials or destination details.</summary>
public interface IExpensePayoutProvider
{
    string Key { get; }
    Task<ExpensePayoutReceipt> PayAsync(ExpensePayoutInstruction instruction, CancellationToken ct = default);
}

public enum ExpensePayoutProviderError
{
    UnsupportedProvider,
    DestinationUnavailable,
    CredentialUnavailable,
    ConfigurationInvalid,
    Rejected,
    SendFailed
}

public sealed class ExpensePayoutProviderException : Exception
{
    public ExpensePayoutProviderException(ExpensePayoutProviderError error)
        : base($"Expense payout provider failed with {error}.") => Error = error;

    public ExpensePayoutProviderException(ExpensePayoutProviderError error, Exception innerException)
        : base($"Expense payout provider failed with {error}.", innerException) => Error = error;

    public ExpensePayoutProviderError Error { get; }
}

/// <summary>Worker-only lease and settlement boundary. Every call rechecks the configured user's membership.</summary>
public interface IExpensePayoutAutomationBridge : INexaModuleBridge
{
    Task<BusinessPage<BusinessMembership>> ListScopesAsync(string userId, int offset = 0, int limit = 50,
        CancellationToken ct = default);
    Task<IReadOnlyList<ExpensePayoutRequest>> ClaimDueAsync(string userId, Guid tenantId, Guid organizationId,
        int limit, TimeSpan leaseDuration, CancellationToken ct = default);
    Task<ExpensePayoutRequest> CompleteAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, Guid version, Guid leaseId, ExpensePayoutReceipt receipt,
        CancellationToken ct = default);
    Task<ExpensePayoutRequest> FailAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, Guid version, Guid leaseId, string errorCode, CancellationToken ct = default);
}
