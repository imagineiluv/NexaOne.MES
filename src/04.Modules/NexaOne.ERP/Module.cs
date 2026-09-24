using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaFramework.Scheduling;
using NexaOne.ERP.Application.Recurring;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP;

/// <summary>
/// ERP의 단일 조립 진입점입니다. Spring XML에는 이 공개 모듈과 공개 bridge만 노출하고,
/// 저장소·업무 서비스의 구현 그래프는 이 클래스 안에 유지합니다.
/// </summary>
public sealed class Module
{
    private readonly IBillingBridge _billingBridge;
    private readonly IExpenseBridge _expenseBridge;
    private readonly IFinancialReportBridge _financialReportBridge;
    private readonly IRecurringBridge _recurringBridge;
    private readonly IRecurringAutomationBridge _recurringAutomationBridge;
    private readonly IHostedService _recurringAutomationWorker;

    public Module(
        EesDataSource dataSource,
        IConfiguration configuration,
        IBusinessMembershipBridge businessMemberships,
        IBusinessMasterDirectory businessMasters,
        IRecurringScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(businessMemberships);
        ArgumentNullException.ThrowIfNull(businessMasters);
        ArgumentNullException.ThrowIfNull(scheduler);

        var bridge = new BillingBridge(dataSource, businessMemberships, businessMasters);
        _billingBridge = bridge;
        _expenseBridge = bridge;
        _financialReportBridge = bridge;
        _recurringBridge = bridge;
        _recurringAutomationBridge = bridge;
        var options = ErpModuleOptions.FromConfiguration(configuration);
        _recurringAutomationWorker = new RecurringAutomationWorker(
            scheduler, bridge, options.RecurringEnabled, options.RecurringPrincipalId,
            TimeSpan.FromSeconds(options.RecurringIntervalSeconds), options.RecurringTimeZone);
    }

    /// <summary>Estimates, invoices and payments over the Framework billing service.</summary>
    public IBillingBridge GetBillingBridge() => _billingBridge;

    /// <summary>Expense directories, entries, reimbursements and invoice linkage.</summary>
    public IExpenseBridge GetExpenseBridge() => _expenseBridge;

    /// <summary>Currency-separated reports plus persisted snapshots, comparisons and usage history.</summary>
    public IFinancialReportBridge GetFinancialReportBridge() => _financialReportBridge;

    /// <summary>Monthly billing, income and expense rules with durable occurrence identity.</summary>
    public IRecurringBridge GetRecurringBridge() => _recurringBridge;

    /// <summary>Non-interactive recurring authority administration and execution.</summary>
    public IRecurringAutomationBridge GetRecurringAutomationBridge() => _recurringAutomationBridge;

    public IHostedService GetRecurringAutomationWorker() => _recurringAutomationWorker;
}

internal sealed record ErpModuleOptions(
    bool RecurringEnabled,
    string RecurringPrincipalId,
    int RecurringIntervalSeconds,
    TimeZoneInfo RecurringTimeZone)
{
    public static ErpModuleOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var enabled = configuration.GetValue("Worker:Erp:Recurring:Enabled", false);
        var principalId = configuration["Worker:Erp:Recurring:PrincipalId"]?.Trim() ?? string.Empty;
        if (enabled && string.IsNullOrWhiteSpace(principalId))
            throw new InvalidOperationException(
                "Worker:Erp:Recurring:PrincipalId is required when recurring automation is enabled.");
        var timeZoneId = configuration["Worker:Erp:Recurring:TimeZoneId"]?.Trim();
        TimeZoneInfo timeZone;
        try
        {
            timeZone = string.IsNullOrWhiteSpace(timeZoneId)
                ? TimeZoneInfo.Utc
                : TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException error)
        {
            throw new InvalidOperationException(
                $"Worker:Erp:Recurring:TimeZoneId '{timeZoneId}' was not found.", error);
        }
        catch (InvalidTimeZoneException error)
        {
            throw new InvalidOperationException(
                $"Worker:Erp:Recurring:TimeZoneId '{timeZoneId}' is invalid.", error);
        }
        return new(enabled, principalId,
            Math.Max(configuration.GetValue("Worker:Erp:Recurring:IntervalSeconds", 3_600), 60),
            timeZone);
    }
}
