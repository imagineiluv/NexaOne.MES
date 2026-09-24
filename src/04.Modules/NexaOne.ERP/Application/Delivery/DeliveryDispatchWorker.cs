using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaFramework.Scheduling;
using NexaOne.ServiceContracts.Collaboration;

namespace NexaOne.ERP.Application.Delivery;

/// <summary>
/// Leases due outbound requests for explicitly granted scopes and settles each provider attempt. External I/O occurs
/// outside the database transaction; cancellation or an uncertain settlement deliberately leaves the lease to expire.
/// </summary>
public sealed class DeliveryDispatchWorker : BackgroundService
{
    internal const string JobName = "collaboration-delivery-dispatch";
    private readonly IRecurringScheduler _scheduler;
    private readonly IDeliveryAutomationBridge _automation;
    private readonly IDeliveryProviderRegistry _providers;
    private readonly bool _enabled;
    private readonly string _principalId;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _leaseDuration;
    private readonly int _batchSize;
    private readonly ILogger _logger;

    public DeliveryDispatchWorker(
        IRecurringScheduler scheduler,
        IDeliveryAutomationBridge automation,
        IDeliveryProviderRegistry providers,
        bool enabled,
        string principalId,
        TimeSpan interval,
        TimeSpan leaseDuration,
        int batchSize,
        ILogger? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _providers = providers ?? throw new ArgumentNullException(nameof(providers));
        _enabled = enabled;
        if (enabled && string.IsNullOrWhiteSpace(principalId))
            throw new ArgumentException("A delivery service principal is required.", nameof(principalId));
        _principalId = principalId ?? string.Empty;
        _interval = interval > TimeSpan.Zero ? interval : throw new ArgumentOutOfRangeException(nameof(interval));
        _leaseDuration = leaseDuration >= TimeSpan.FromSeconds(10) && leaseDuration <= TimeSpan.FromMinutes(15)
            ? leaseDuration
            : throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        _batchSize = batchSize is >= 1 and <= 100
            ? batchSize
            : throw new ArgumentOutOfRangeException(nameof(batchSize));
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
        const int scopePageSize = 100;
        var offset = 0;
        while (true)
        {
            BusinessPage<DeliveryServicePrincipalScope> scopes;
            try
            {
                scopes = await _automation.ListActiveScopesAsync(_principalId, offset, scopePageSize, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _logger.LogError(error,
                    "Delivery principal {PrincipalId} could not list active scopes.", _principalId);
                return;
            }

            foreach (var scope in scopes.Items)
                await RunScopeAsync(scope, ct);
            offset += scopes.Items.Count;
            if (offset >= scopes.Total || scopes.Items.Count == 0) return;
        }
    }

    private async Task RunScopeAsync(DeliveryServicePrincipalScope scope, CancellationToken ct)
    {
        while (true)
        {
            IReadOnlyList<DeliveryRequest> claimed;
            try
            {
                claimed = await _automation.ClaimDueAsync(
                    _principalId, scope.TenantId, scope.OrganizationId, scope.Version,
                    _batchSize, _leaseDuration, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (BusinessException error) when (error.Code == "BUSINESS_ACCESS_DENIED")
            {
                _logger.LogWarning(
                    "Delivery authority for {PrincipalId} in {TenantId}/{OrganizationId} was revoked.",
                    _principalId, scope.TenantId, scope.OrganizationId);
                return;
            }
            catch (Exception error)
            {
                _logger.LogError(error,
                    "Delivery claims failed for {TenantId}/{OrganizationId}.",
                    scope.TenantId, scope.OrganizationId);
                return;
            }

            foreach (var request in claimed)
                await DispatchAsync(scope, request, ct);
            if (claimed.Count < _batchSize) return;
        }
    }

    private async Task DispatchAsync(
        DeliveryServicePrincipalScope scope, DeliveryRequest request, CancellationToken ct)
    {
        if (request.LeaseId is not { } leaseId)
        {
            _logger.LogError("Claimed delivery {DeliveryId} had no lease identifier.", request.Id);
            return;
        }

        string receipt;
        try
        {
            receipt = await _providers.GetRequired(request.Payload.ProviderKey)
                .SendAsync(request.Payload, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (DeliveryProviderException error)
        {
            await RecordFailureAsync(scope, request, leaseId, Normalize(error.Error), ct);
            return;
        }
        catch (Exception error)
        {
            _logger.LogError(error, "Delivery provider failed unexpectedly for request {DeliveryId}.", request.Id);
            await RecordFailureAsync(scope, request, leaseId, "DELIVERY_PROVIDER_UNEXPECTED", ct);
            return;
        }

        try
        {
            await _automation.CompleteAsync(
                _principalId, scope.TenantId, scope.OrganizationId, scope.Version,
                request.Id, request.Version, leaseId, receipt, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            // Provider acceptance is already externally visible. Do not convert an uncertain settlement into a
            // provider failure; the durable lease will expire and surface the at-least-once recovery path.
            _logger.LogError(error,
                "Accepted delivery {DeliveryId} could not be settled before lease recovery.", request.Id);
        }
    }

    private async Task RecordFailureAsync(
        DeliveryServicePrincipalScope scope, DeliveryRequest request, Guid leaseId,
        string errorCode, CancellationToken ct)
    {
        try
        {
            await _automation.FailAsync(
                _principalId, scope.TenantId, scope.OrganizationId, scope.Version,
                request.Id, request.Version, leaseId, errorCode, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            _logger.LogError(error,
                "Failed delivery {DeliveryId} could not be settled before lease recovery.", request.Id);
        }
    }

    private static string Normalize(DeliveryProviderError error) => error switch
    {
        DeliveryProviderError.UnsupportedProvider => "DELIVERY_PROVIDER_UNSUPPORTED",
        DeliveryProviderError.CredentialUnavailable => "DELIVERY_CREDENTIAL_UNAVAILABLE",
        DeliveryProviderError.ConfigurationInvalid => "DELIVERY_PROVIDER_CONFIGURATION_INVALID",
        DeliveryProviderError.InvalidRecipient => "DELIVERY_RECIPIENT_INVALID",
        DeliveryProviderError.SendFailed => "DELIVERY_PROVIDER_SEND_FAILED",
        _ => "DELIVERY_PROVIDER_UNEXPECTED"
    };
}
