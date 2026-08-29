using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Infrastructure;
using NexaOne.ServiceContracts.Fdc;
using NexaOne.ServiceContracts.Ivt;
using NexaDB.Data.Abstractions.Interfaces;

namespace NexaOne.IVT;

/// <summary>
/// IVT의 단일 조립 진입점입니다. Spring XML에는 이 공개 모듈과 공개 bridge/worker만 노출하고,
/// 저장소·업무 서비스의 구현 그래프는 이 클래스 안에 유지합니다.
/// </summary>
public sealed class Module
{
    private readonly IMaterialBridge _materialBridge;
    private readonly IMaterialLotBridge _materialLotBridge;
    private readonly ITraceMaterialBridge _traceMaterialBridge;
    private readonly IMaterialLotDirectory _materialLotDirectory;
    private readonly IMrpInventoryDirectory _mrpInventoryDirectory;
    private readonly IFdcTraceRetentionGuard _fdcTraceRetentionGuard;
    private readonly IHostedService _traceMaterialConsumptionWorker;

    public Module(
        EesDataSource dataSource,
        INexaOneEESDbCapability dialect,
        IFdcTraceSource traceSource,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(dialect);
        ArgumentNullException.ThrowIfNull(traceSource);
        ArgumentNullException.ThrowIfNull(configuration);

        var consumptionService = new ConsumptionService(new ConsumptionRepository(dataSource));
        var materialLotRepository = new MaterialLotRepository(dataSource);
        var traceRepository = new TraceProjectionRepository(dataSource, dialect);
        var bindingsEnabled = configuration.GetValue(
            "Ivt:TraceConfiguration:BindingsEnabled", false);
        if (bindingsEnabled)
        {
            throw new InvalidOperationException(
                "Ivt:TraceConfiguration:BindingsEnabled=true is not supported until the durable "
                + "cross-process maintenance fence excludes FDC collection, retention, and IVT projection.");
        }

        _materialBridge = new MaterialBridge(consumptionService);
        _materialLotBridge = new MaterialLotBridge(
            new MaterialLotService(materialLotRepository));
        _traceMaterialBridge = new TraceMaterialBridge(
            new TraceBindingService(
                new TraceBindingRepository(dataSource),
                traceSource,
                TraceMaintenanceGate.From(configuration)),
            new FeedSessionService(
                new FeedSessionRepository(dataSource),
                materialLotRepository),
            bindingsEnabled,
            configuration.GetValue("Ivt:TraceConfiguration:FeedSessionsEnabled", false));
        _materialLotDirectory = new MaterialLotDirectory(dataSource);
        _mrpInventoryDirectory = new MrpInventoryDirectory(dataSource);
        _fdcTraceRetentionGuard = new FdcTraceRetentionGuard(dataSource);
        _traceMaterialConsumptionWorker = new TraceMaterialConsumptionWorker(
            new TraceIngestionService(traceSource, traceRepository),
            traceRepository,
            consumptionService,
            configuration);
    }

    /// <summary>자재 소비/취소 bridge의 모듈 singleton을 반환합니다.</summary>
    public IMaterialBridge GetMaterialBridge() => _materialBridge;

    /// <summary>자재 LOT 수명주기 bridge의 모듈 singleton을 반환합니다.</summary>
    public IMaterialLotBridge GetMaterialLotBridge() => _materialLotBridge;

    /// <summary>TRACE binding 및 자재 장착 세션 bridge의 모듈 singleton을 반환합니다.</summary>
    public ITraceMaterialBridge GetTraceMaterialBridge() => _traceMaterialBridge;

    /// <summary>자재 LOT 검사 참조용 축소 directory의 모듈 singleton을 반환합니다.</summary>
    public IMaterialLotDirectory GetMaterialLotDirectory() => _materialLotDirectory;

    /// <summary>MRP 계산용 품목별 가용 재고 snapshot을 반환합니다.</summary>
    public IMrpInventoryDirectory GetMrpInventoryDirectory() => _mrpInventoryDirectory;

    /// <summary>활성 IVT ingestion 범위가 요구하는 FDC raw TRACE 보존 경계를 반환합니다.</summary>
    public IFdcTraceRetentionGuard GetFdcTraceRetentionGuard() => _fdcTraceRetentionGuard;

    /// <summary>영속 TRACE를 자재 소비 원장에 투영하는 모듈 worker를 반환합니다.</summary>
    public IHostedService GetTraceMaterialConsumptionWorker() => _traceMaterialConsumptionWorker;
}
