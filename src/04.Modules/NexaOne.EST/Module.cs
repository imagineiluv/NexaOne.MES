using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Application.Oee;
using NexaOne.EST.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Mdm;
using NexaDB.Data.Abstractions.Interfaces;
using NexaFramework.Scheduling;

namespace NexaOne.EST;

/// <summary>EST 내부 저장소·집계 그래프를 숨기고 설비 상태 공개 인터페이스만 노출하는 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IEquipmentAlarmBridge _alarmBridge;
    private readonly IEquipmentStateBridge _stateBridge;
    private readonly IEquipmentOutputBridge _outputBridge;
    private readonly IUtilityBridge _utilityBridge;
    private readonly IOeeAggregationBridge _oeeBridge;
    private readonly IHostedService _oeeWorker;

    public Module(
        EesDataSource dataSource,
        INexaOneEESDbCapability dialect,
        IConfiguration configuration,
        IEquipmentDirectory equipmentDirectory,
        IEquipmentOutputMasterDirectory outputMasterDirectory,
        IOeeEvidenceSource oeeEvidenceSource,
        IRecurringScheduler scheduler)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(equipmentDirectory);
        ArgumentNullException.ThrowIfNull(outputMasterDirectory);
        ArgumentNullException.ThrowIfNull(oeeEvidenceSource);
        ArgumentNullException.ThrowIfNull(scheduler);

        _alarmBridge = new EquipmentAlarmBridge(new EquipmentAlarmService(
            new EquipmentAlarmRepository(dataSource, configuration),
            equipmentDirectory));
        _stateBridge = new EquipmentStateBridge(new EquipmentStateService(
            new EquipmentStateMatrixRepository(dataSource),
            new EquipmentStateRepository(dataSource, dialect, configuration)));
        _outputBridge = new EquipmentOutputBridge(new EquipmentOutputService(
            new EquipmentOutputRepository(dataSource),
            outputMasterDirectory));
        _utilityBridge = new UtilityBridge(new UtilityService(
            new UtilityRepository(dataSource, dialect)));

        var oee = new OeeAggregationRepository(dataSource, oeeEvidenceSource);
        _oeeBridge = new OeeAggregationBridge(oee);
        _oeeWorker = new OeeAggregationWorker(scheduler, oee, configuration);
    }

    public IEquipmentAlarmBridge GetAlarmBridge() => _alarmBridge;
    public IEquipmentStateBridge GetStateBridge() => _stateBridge;
    public IEquipmentOutputBridge GetOutputBridge() => _outputBridge;
    public IUtilityBridge GetUtilityBridge() => _utilityBridge;
    public IOeeAggregationBridge GetOeeBridge() => _oeeBridge;
    public IHostedService GetOeeWorker() => _oeeWorker;
}
