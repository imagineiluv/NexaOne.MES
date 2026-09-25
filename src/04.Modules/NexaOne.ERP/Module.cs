using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaFramework.Scheduling;
using NexaOne.ERP.Application.Delivery;
using NexaOne.ERP.Application.Expense;
using NexaOne.ERP.Application.Recurring;
using NexaOne.ERP.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Collaboration;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Ivt;
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
    private readonly IExpensePayoutAutomationBridge _expensePayoutAutomationBridge;
    private readonly IFinancialReportBridge _financialReportBridge;
    private readonly IBusinessReportBridge _businessReportBridge;
    private readonly IRecurringBridge _recurringBridge;
    private readonly IRecurringAutomationBridge _recurringAutomationBridge;
    private readonly IDeliveryBridge _deliveryBridge;
    private readonly IDeliveryAutomationBridge _deliveryAutomationBridge;
    private readonly ISqliteSchemaContribution _billingSqliteSchemaContribution;
    private readonly IHostedService _recurringAutomationWorker;
    private readonly IHostedService _deliveryDispatchWorker;
    private readonly IHostedService _expensePayoutWorker;

    public Module(
        EesDataSource dataSource,
        IConfiguration configuration,
        IBusinessMembershipBridge businessMemberships,
        IBusinessMasterDirectory businessMasters,
        IBusinessProjectDirectory businessProjects,
        IStockBillingDirectory stockBillingDirectory,
        IRecurringScheduler scheduler,
        IDeliveryProviderRegistry deliveryProviders,
        IExpensePayoutProviderRegistry expensePayoutProviders)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(businessMemberships);
        ArgumentNullException.ThrowIfNull(businessMasters);
        ArgumentNullException.ThrowIfNull(businessProjects);
        ArgumentNullException.ThrowIfNull(stockBillingDirectory);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(deliveryProviders);
        ArgumentNullException.ThrowIfNull(expensePayoutProviders);

        var bridge = new BillingBridge(dataSource, businessMemberships, businessMasters,
            projects: businessProjects, stockBilling: stockBillingDirectory);
        _billingBridge = bridge;
        _expenseBridge = bridge;
        _expensePayoutAutomationBridge = bridge;
        _financialReportBridge = bridge;
        _businessReportBridge = bridge;
        _recurringBridge = bridge;
        _recurringAutomationBridge = bridge;
        _deliveryBridge = bridge;
        _deliveryAutomationBridge = bridge;
        _billingSqliteSchemaContribution = new ErpBillingSqliteSchemaContribution();
        var options = ErpModuleOptions.FromConfiguration(configuration);
        _recurringAutomationWorker = new RecurringAutomationWorker(
            scheduler, bridge, options.RecurringEnabled, options.RecurringPrincipalId,
            TimeSpan.FromSeconds(options.RecurringIntervalSeconds), options.RecurringTimeZone);
        _deliveryDispatchWorker = new DeliveryDispatchWorker(
            scheduler, bridge, deliveryProviders, options.DeliveryEnabled, options.DeliveryPrincipalId,
            TimeSpan.FromSeconds(options.DeliveryIntervalSeconds),
            TimeSpan.FromSeconds(options.DeliveryLeaseSeconds), options.DeliveryBatchSize);
        _expensePayoutWorker = new ExpensePayoutWorker(
            scheduler, bridge, expensePayoutProviders, options.ExpensePayoutEnabled, options.ExpensePayoutUserId,
            TimeSpan.FromSeconds(options.ExpensePayoutIntervalSeconds),
            TimeSpan.FromSeconds(options.ExpensePayoutLeaseSeconds), options.ExpensePayoutBatchSize);
    }

    /// <summary>Estimates, invoices, credit notes and payments over the Framework billing service.</summary>
    public IBillingBridge GetBillingBridge() => _billingBridge;

    /// <summary>Expense directories, entries, reimbursements and invoice linkage.</summary>
    public IExpenseBridge GetExpenseBridge() => _expenseBridge;

    /// <summary>Non-interactive expense payout lease settlement.</summary>
    public IExpensePayoutAutomationBridge GetExpensePayoutAutomationBridge() =>
        _expensePayoutAutomationBridge;

    /// <summary>Currency-separated reports plus persisted snapshots, comparisons and usage history.</summary>
    public IFinancialReportBridge GetFinancialReportBridge() => _financialReportBridge;

    /// <summary>Registered organization-scoped ERP reports with calendar aggregation and CSV export.</summary>
    public IBusinessReportBridge GetBusinessReportBridge() => _businessReportBridge;

    /// <summary>Monthly billing, income and expense rules with durable occurrence identity.</summary>
    public IRecurringBridge GetRecurringBridge() => _recurringBridge;

    /// <summary>Non-interactive recurring authority administration and execution.</summary>
    public IRecurringAutomationBridge GetRecurringAutomationBridge() => _recurringAutomationBridge;

    /// <summary>Durable plain-text templates, provider references and outbound delivery requests.</summary>
    public IDeliveryBridge GetDeliveryBridge() => _deliveryBridge;

    /// <summary>Non-interactive delivery authority and lease settlement.</summary>
    public IDeliveryAutomationBridge GetDeliveryAutomationBridge() => _deliveryAutomationBridge;

    public ISqliteSchemaContribution GetBillingSqliteSchemaContribution() =>
        _billingSqliteSchemaContribution;

    public IHostedService GetRecurringAutomationWorker() => _recurringAutomationWorker;

    public IHostedService GetDeliveryDispatchWorker() => _deliveryDispatchWorker;

    public IHostedService GetExpensePayoutWorker() => _expensePayoutWorker;
}

internal sealed record ErpModuleOptions(
    bool RecurringEnabled,
    string RecurringPrincipalId,
    int RecurringIntervalSeconds,
    TimeZoneInfo RecurringTimeZone,
    bool DeliveryEnabled,
    string DeliveryPrincipalId,
    int DeliveryIntervalSeconds,
    int DeliveryLeaseSeconds,
    int DeliveryBatchSize,
    bool ExpensePayoutEnabled,
    string ExpensePayoutUserId,
    int ExpensePayoutIntervalSeconds,
    int ExpensePayoutLeaseSeconds,
    int ExpensePayoutBatchSize)
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
        var deliveryEnabled = configuration.GetValue("Worker:Collaboration:Delivery:Enabled", false);
        var deliveryPrincipalId = configuration["Worker:Collaboration:Delivery:PrincipalId"]?.Trim()
            ?? string.Empty;
        if (deliveryEnabled && string.IsNullOrWhiteSpace(deliveryPrincipalId))
            throw new InvalidOperationException(
                "Worker:Collaboration:Delivery:PrincipalId is required when delivery dispatch is enabled.");
        var deliveryLeaseSeconds = configuration.GetValue("Worker:Collaboration:Delivery:LeaseSeconds", 60);
        if (deliveryLeaseSeconds is < 10 or > 900)
            throw new InvalidOperationException(
                "Worker:Collaboration:Delivery:LeaseSeconds must be between 10 and 900.");
        var deliveryBatchSize = configuration.GetValue("Worker:Collaboration:Delivery:BatchSize", 25);
        if (deliveryBatchSize is < 1 or > 100)
            throw new InvalidOperationException(
                "Worker:Collaboration:Delivery:BatchSize must be between 1 and 100.");
        var payoutEnabled = configuration.GetValue("Worker:Erp:ExpensePayout:Enabled", false);
        var payoutUserId = configuration["Worker:Erp:ExpensePayout:UserId"]?.Trim() ?? string.Empty;
        if (payoutEnabled && string.IsNullOrWhiteSpace(payoutUserId))
            throw new InvalidOperationException(
                "Worker:Erp:ExpensePayout:UserId is required when expense payout is enabled.");
        var payoutLeaseSeconds = configuration.GetValue("Worker:Erp:ExpensePayout:LeaseSeconds", 60);
        if (payoutLeaseSeconds is < 10 or > 900)
            throw new InvalidOperationException(
                "Worker:Erp:ExpensePayout:LeaseSeconds must be between 10 and 900.");
        var payoutBatchSize = configuration.GetValue("Worker:Erp:ExpensePayout:BatchSize", 25);
        if (payoutBatchSize is < 1 or > 100)
            throw new InvalidOperationException(
                "Worker:Erp:ExpensePayout:BatchSize must be between 1 and 100.");
        return new(enabled, principalId,
            Math.Max(configuration.GetValue("Worker:Erp:Recurring:IntervalSeconds", 3_600), 60),
            timeZone, deliveryEnabled, deliveryPrincipalId,
            Math.Max(configuration.GetValue("Worker:Collaboration:Delivery:IntervalSeconds", 30), 10),
            deliveryLeaseSeconds, deliveryBatchSize, payoutEnabled, payoutUserId,
            Math.Max(configuration.GetValue("Worker:Erp:ExpensePayout:IntervalSeconds", 30), 10),
            payoutLeaseSeconds, payoutBatchSize);
    }
}
