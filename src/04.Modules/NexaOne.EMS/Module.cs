using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.EMS.Application.Ems;
using NexaOne.EMS.Application.MaintenanceExecution;
using NexaOne.EMS.Application.MaintenanceSchedules;
using NexaOne.EMS.Application.SpareParts;
using NexaOne.EMS.Application.Tools;
using NexaOne.EMS.Infrastructure;
using NexaOne.Infrastructure.Messaging;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ems;
using NexaOne.ServiceContracts.Mdm;
using NexaFramework.Scheduling;

namespace NexaOne.EMS;

/// <summary>EMS 내부 저장소·보전 그래프를 숨기고 공개 bridge와 worker만 노출하는 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IEmsBridge _emsBridge;
    private readonly IMaintenanceScheduleBridge _scheduleBridge;
    private readonly IMaintenanceExecutionBridge _executionBridge;
    private readonly ISparePartBridge _sparePartBridge;
    private readonly IToolBridge _toolBridge;
    private readonly IHostedService _dueCheckWorker;

    public Module(
        EesDataSource dataSource,
        IConfiguration configuration,
        IVendorDirectory vendorDirectory,
        IEquipmentDirectory equipmentDirectory,
        IEquipmentOutputMasterDirectory equipmentOutputMasterDirectory,
        IMaintenanceIdentityDirectory maintenanceIdentityDirectory,
        IRecurringScheduler scheduler,
        IMessageBus messageBus)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(vendorDirectory);
        ArgumentNullException.ThrowIfNull(equipmentDirectory);
        ArgumentNullException.ThrowIfNull(equipmentOutputMasterDirectory);
        ArgumentNullException.ThrowIfNull(maintenanceIdentityDirectory);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(messageBus);

        var options = EmsModuleOptions.FromConfiguration(configuration);

        var workOrders = new WorkOrderRepository(dataSource, configuration);
        var maintenancePlans = new MaintenancePlanRepository(dataSource, configuration);
        _emsBridge = new EmsBridge(
            new EmsService(workOrders, maintenancePlans),
            new MaintenancePlanService(
                maintenancePlans,
                new SparePartRepository(dataSource),
                equipmentDirectory));
        _scheduleBridge = new MaintenanceScheduleBridge(
            new MaintenanceScheduleService(new MaintenanceScheduleRepository(dataSource)));
        _executionBridge = new MaintenanceExecutionBridge(
            new MaintenanceExecutionService(
                new MaintenanceExecutionRepository(dataSource),
                maintenanceIdentityDirectory));
        _sparePartBridge = new SparePartBridge(new SparePartService(
            new SparePartManagementRepository(dataSource),
            vendorDirectory,
            equipmentDirectory));
        _toolBridge = new ToolBridge(new ToolService(
            new ToolRepository(dataSource),
            equipmentDirectory,
            equipmentOutputMasterDirectory));
        _dueCheckWorker = new MaintenanceDueCheckWorker(
            scheduler,
            maintenancePlans,
            messageBus,
            enabled: options.MaintenanceDueEnabled,
            intervalSeconds: options.MaintenanceDueIntervalSeconds,
            topic: options.EventTopic);
    }

    public IEmsBridge GetEmsBridge() => _emsBridge;
    public IMaintenanceScheduleBridge GetScheduleBridge() => _scheduleBridge;
    public IMaintenanceExecutionBridge GetExecutionBridge() => _executionBridge;
    public ISparePartBridge GetSparePartBridge() => _sparePartBridge;
    public IToolBridge GetToolBridge() => _toolBridge;
    public IHostedService GetDueCheckWorker() => _dueCheckWorker;
}

/// <summary>
/// Spring XML에 실행 정책 상수를 노출하지 않도록 EMS worker 설정을 한곳에서 정규화합니다.
/// 활성화 키가 없으면 OFF이고, 지나치게 짧은 점검 주기는 60초로 제한합니다.
/// </summary>
internal sealed record EmsModuleOptions(
    bool MaintenanceDueEnabled,
    int MaintenanceDueIntervalSeconds,
    string EventTopic)
{
    private const string DefaultEventTopic = "nexaone.events";

    public static EmsModuleOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return new EmsModuleOptions(
            MaintenanceDueEnabled: configuration.GetValue(
                "Worker:Ems:MaintenanceDue:Enabled",
                false),
            MaintenanceDueIntervalSeconds: Math.Max(
                configuration.GetValue("Worker:Ems:MaintenanceDue:IntervalSeconds", 3_600),
                60),
            EventTopic: FirstNonBlank(
                configuration["Worker:Ems:MaintenanceDue:Topic"],
                configuration["Events:Outbox:Topic"],
                DefaultEventTopic));
    }

    private static string FirstNonBlank(params string?[] candidates) =>
        candidates
            .Select(static candidate => candidate?.Trim())
            .First(static candidate => !string.IsNullOrWhiteSpace(candidate))!;
}
