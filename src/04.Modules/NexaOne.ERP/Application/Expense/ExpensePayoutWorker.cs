using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexaFramework.Service;
using NexaFramework.Scheduling;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Application.Expense;

/// <summary>Leases durable payout requests and performs external I/O outside the database transaction.</summary>
public sealed class ExpensePayoutWorker : BackgroundService
{
    internal const string JobName = "erp-expense-payout";
    private readonly IRecurringScheduler _scheduler;
    private readonly IExpensePayoutAutomationBridge _automation;
    private readonly IExpensePayoutProviderRegistry _providers;
    private readonly bool _enabled;
    private readonly string _userId;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _leaseDuration;
    private readonly int _batchSize;
    private readonly ILogger _logger;

    public ExpensePayoutWorker(IRecurringScheduler scheduler, IExpensePayoutAutomationBridge automation,
        IExpensePayoutProviderRegistry providers, bool enabled, string userId, TimeSpan interval,
        TimeSpan leaseDuration, int batchSize, ILogger? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _enabled = enabled;
        if (enabled && string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("An expense payout user is required.", nameof(userId));
        _userId = userId ?? string.Empty;
        _interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval));
        _leaseDuration = leaseDuration is { TotalSeconds: >= 10, TotalMinutes: <= 15 }
            ? leaseDuration : throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        _batchSize = batchSize is >= 1 and <= 100 ? batchSize : throw new ArgumentOutOfRangeException(nameof(batchSize));
        _logger = logger ?? NullLogger.Instance;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;
        await _scheduler.StartAsync(stoppingToken);
        await _scheduler.ScheduleRecurringAsync(JobName, _interval, RunAsync, stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_enabled) await _scheduler.UnscheduleAsync(JobName, cancellationToken);
        await base.StopAsync(cancellationToken);
    }

    internal async Task RunAsync(CancellationToken ct)
    {
        var offset = 0;
        while (true)
        {
            BusinessPage<NexaOne.ServiceContracts.Sys.BusinessMembership> scopes;
            try { scopes = await _automation.ListScopesAsync(_userId, offset, 100, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _logger.LogError(error, "Expense payout user {UserId} could not list active scopes.", _userId);
                return;
            }
            foreach (var scope in scopes.Items) await RunScopeAsync(scope.TenantId, scope.OrganizationId, ct);
            offset += scopes.Items.Count;
            if (offset >= scopes.Total || scopes.Items.Count == 0) return;
        }
    }

    private async Task RunScopeAsync(Guid tenantId, Guid organizationId, CancellationToken ct)
    {
        while (true)
        {
            IReadOnlyList<ExpensePayoutRequest> claimed;
            try
            {
                claimed = await _automation.ClaimDueAsync(_userId, tenantId, organizationId,
                    _batchSize, _leaseDuration, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _logger.LogError(error, "Expense payout claims failed for {TenantId}/{OrganizationId}.",
                    tenantId, organizationId);
                return;
            }
            foreach (var request in claimed) await DispatchAsync(request, ct);
            if (claimed.Count < _batchSize) return;
        }
    }

    private async Task DispatchAsync(ExpensePayoutRequest request, CancellationToken ct)
    {
        if (request.LeaseId is not { } leaseId)
        {
            _logger.LogError("Claimed expense payout {PayoutId} had no lease identifier.", request.Id);
            return;
        }
        ExpensePayoutReceipt receipt;
        try
        {
            receipt = await _providers.GetRequired(request.ProviderKey).PayAsync(new(
                request.OperationId, Guid.Parse(request.Scope.TenantId), Guid.Parse(request.Scope.OrganizationId),
                request.EmployeeId, request.Amount, request.Currency), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ExpensePayoutProviderException error)
        {
            await RecordFailureAsync(request, leaseId, Normalize(error.Error), ct);
            return;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Expense payout provider failed unexpectedly for {PayoutId}.", request.Id);
            await RecordFailureAsync(request, leaseId, "EXPENSE_PAYOUT_PROVIDER_UNEXPECTED", ct);
            return;
        }
        try
        {
            await _automation.CompleteAsync(_userId, Guid.Parse(request.Scope.TenantId),
                Guid.Parse(request.Scope.OrganizationId), request.Id, request.Version, leaseId, receipt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Provider acceptance is externally visible. Keep the lease for idempotent recovery instead of
            // misclassifying an uncertain database settlement as a provider failure.
            _logger.LogError(error, "Accepted expense payout {PayoutId} could not be settled.", request.Id);
        }
    }

    private async Task RecordFailureAsync(ExpensePayoutRequest request, Guid leaseId,
        string errorCode, CancellationToken ct)
    {
        try
        {
            await _automation.FailAsync(_userId, Guid.Parse(request.Scope.TenantId),
                Guid.Parse(request.Scope.OrganizationId), request.Id, request.Version, leaseId, errorCode, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger.LogError(error, "Failed expense payout {PayoutId} could not be settled.", request.Id);
        }
    }

    private static string Normalize(ExpensePayoutProviderError error) => error switch
    {
        ExpensePayoutProviderError.UnsupportedProvider => "EXPENSE_PAYOUT_PROVIDER_UNSUPPORTED",
        ExpensePayoutProviderError.DestinationUnavailable => "EXPENSE_PAYOUT_DESTINATION_UNAVAILABLE",
        ExpensePayoutProviderError.CredentialUnavailable => "EXPENSE_PAYOUT_CREDENTIAL_UNAVAILABLE",
        ExpensePayoutProviderError.ConfigurationInvalid => "EXPENSE_PAYOUT_PROVIDER_CONFIGURATION_INVALID",
        ExpensePayoutProviderError.Rejected => "EXPENSE_PAYOUT_REJECTED",
        ExpensePayoutProviderError.SendFailed => "EXPENSE_PAYOUT_PROVIDER_SEND_FAILED",
        _ => "EXPENSE_PAYOUT_PROVIDER_UNEXPECTED"
    };
}
