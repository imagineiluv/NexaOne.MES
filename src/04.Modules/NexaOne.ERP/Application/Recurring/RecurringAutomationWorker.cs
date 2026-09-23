using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaFramework.Scheduling;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Application.Recurring;

/// <summary>
/// Registers one host job that executes due months in each scope's local calendar. Scope and execution calls
/// independently recheck live persisted authority and the scope version used to choose those months.
/// </summary>
public sealed class RecurringAutomationWorker : BackgroundService
{
    internal const string JobName = "erp-recurring-occurrences";
    private readonly IRecurringScheduler _scheduler;
    private readonly IRecurringAutomationBridge _automation;
    private readonly bool _enabled;
    private readonly string _principalId;
    private readonly TimeSpan _interval;
    private readonly TimeZoneInfo _defaultTimeZone;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    public RecurringAutomationWorker(
        IRecurringScheduler scheduler,
        IRecurringAutomationBridge automation,
        bool enabled,
        string principalId,
        TimeSpan interval,
        TimeZoneInfo timeZone,
        TimeProvider? clock = null,
        ILogger? logger = null)
    {
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _automation = automation ?? throw new ArgumentNullException(nameof(automation));
        _enabled = enabled;
        if (enabled && string.IsNullOrWhiteSpace(principalId))
            throw new ArgumentException("A recurring service principal is required.", nameof(principalId));
        _principalId = principalId ?? string.Empty;
        _interval = interval > TimeSpan.Zero
            ? interval
            : throw new ArgumentOutOfRangeException(nameof(interval));
        _defaultTimeZone = timeZone ?? throw new ArgumentNullException(nameof(timeZone));
        _clock = clock ?? TimeProvider.System;
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
        const int pageSize = 100;
        var scopeOffset = 0;
        while (true)
        {
            BusinessPage<RecurringServicePrincipalScope> scopes;
            try
            {
                scopes = await _automation.ListActiveScopesAsync(
                    _principalId, scopeOffset, pageSize, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _logger.LogError(error,
                    "Recurring ERP principal {PrincipalId} could not list active scopes.", _principalId);
                return;
            }

            foreach (var scope in scopes.Items)
                await RunScopeAsync(scope, ct);
            scopeOffset += scopes.Items.Count;
            if (scopeOffset >= scopes.Total || scopes.Items.Count == 0) return;
        }
    }

    private async Task RunScopeAsync(RecurringServicePrincipalScope scope, CancellationToken ct)
    {
        TimeZoneInfo timeZone;
        try
        {
            timeZone = string.IsNullOrWhiteSpace(scope.TimeZoneId)
                ? _defaultTimeZone
                : TimeZoneInfo.FindSystemTimeZoneById(scope.TimeZoneId);
        }
        catch (Exception error) when (error is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            _logger.LogError(error,
                "Recurring ERP scope {TenantId}/{OrganizationId} has invalid time zone {TimeZoneId}.",
                scope.TenantId, scope.OrganizationId, scope.TimeZoneId);
            return;
        }

        var localDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            _clock.GetUtcNow(), timeZone).DateTime);
        var currentMonth = new DateOnly(localDate.Year, localDate.Month, 1);
        for (var monthsAgo = scope.CatchUpMonths; monthsAgo >= 0; monthsAgo--)
        {
            var month = currentMonth.AddMonths(-monthsAgo);
            var throughDate = month == currentMonth
                ? localDate
                : new DateOnly(month.Year, month.Month, DateTime.DaysInMonth(month.Year, month.Month));
            if (!await RunMonthAsync(scope, month, throughDate, ct)) return;
        }
    }

    private async Task<bool> RunMonthAsync(
        RecurringServicePrincipalScope scope, DateOnly month, DateOnly throughDate, CancellationToken ct)
    {
        const int pageSize = 100;
        var ruleOffset = 0;
        while (true)
        {
            BusinessPage<RecurringRule> rules;
            try
            {
                rules = await _automation.ListDueRulesAsync(
                    _principalId, scope.TenantId, scope.OrganizationId, scope.Version, throughDate,
                    ruleOffset, pageSize, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                _logger.LogWarning(error,
                    "Recurring ERP scope {TenantId}/{OrganizationId} is no longer executable by {PrincipalId}.",
                    scope.TenantId, scope.OrganizationId, _principalId);
                return false;
            }

            foreach (var rule in rules.Items)
            {
                try
                {
                    await _automation.ExecuteOccurrenceAsync(
                        _principalId, scope.TenantId, scope.OrganizationId, scope.Version,
                        rule.Id, month, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (BusinessException error) when (error.Code == "BUSINESS_ACCESS_DENIED")
                {
                    _logger.LogWarning(
                        "Recurring ERP authority for {PrincipalId} in {TenantId}/{OrganizationId} was revoked.",
                        _principalId, scope.TenantId, scope.OrganizationId);
                    return false;
                }
                catch (Exception error)
                {
                    _logger.LogError(error,
                        "Recurring ERP rule {RuleId} failed for {TenantId}/{OrganizationId} in {Month:yyyy-MM}.",
                        rule.Id, scope.TenantId, scope.OrganizationId, month);
                }
            }

            ruleOffset += rules.Items.Count;
            if (ruleOffset >= rules.Total || rules.Items.Count == 0) return true;
        }
    }
}
