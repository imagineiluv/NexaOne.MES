using NexaOne.API.Hubs;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Infrastructure.Equipment;
using NexusFramework;

namespace NexaOne.API.Services;

/// <summary>
/// FDC 실시간 수집 호스트 (design 10.4.2 / 10.4.3).
/// 활성 설비-엔드포인트(FDC_EQUIPMENT_ENDPOINT)를 로드해 NexusFramework <see cref="Machine"/> +
/// <see cref="OpcUaDeviceInterface"/>로 구성하고, <see cref="PlantController"/>로 lifecycle을 관리하며
/// <see cref="FdcCollectorService"/>로 태그를 구독한다. 인터락 발동 시 SignalR 알림 + 해당 설비 정지.
/// </summary>
/// <remarks>
/// 실제 OPC-UA 서버 연결을 시도하므로 <c>"Fdc:Collector:Enabled" = true</c>일 때만 기동한다(기본 비활성).
/// 개발/CI 환경에서 서버 없이 자동 기동해 연결 실패로 Abort되는 것을 방지한다.
/// </remarks>
public sealed class FdcCollectorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IEesHubNotifier _notifier;
    private readonly IConfiguration _config;
    private readonly ILogger<FdcCollectorHostedService> _logger;

    private readonly PlantController _plant;   // 싱글톤 공유 — FdcController가 수동 제어(§10.4.4)
    private readonly Dictionary<string, List<string>> _equipmentMachines = new();  // equipmentId -> machine(endpoint) names
    private IServiceScope? _scope;

    public FdcCollectorHostedService(
        PlantController plant,
        IServiceScopeFactory scopeFactory,
        IEesHubNotifier notifier,
        IConfiguration config,
        ILogger<FdcCollectorHostedService> logger)
    {
        _plant = plant;
        _scopeFactory = scopeFactory;
        _notifier = notifier;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_config.GetValue("Fdc:Collector:Enabled", false))
        {
            _logger.LogInformation("FDC collector is disabled (Fdc:Collector:Enabled=false). Skipping startup.");
            return;
        }

        _scope = _scopeFactory.CreateScope();
        var sp = _scope.ServiceProvider;
        var endpointRepo = sp.GetRequiredService<IFdcEquipmentEndpointRepository>();
        var paramRepo = sp.GetRequiredService<IFdcParameterRepository>();
        var collector = sp.GetRequiredService<FdcCollectorService>();

        collector.InterlockTriggered += OnInterlockTriggered;
        collector.InterlockResolved += OnInterlockResolved;
        collector.AlarmRaised += OnAlarmRaised;
        collector.AlarmCleared += OnAlarmCleared;

        var endpoints = await endpointRepo.GetAllActiveAsync(stoppingToken);
        var devices = new List<OpcUaDeviceInterface>();

        foreach (var ep in endpoints)
        {
            try
            {
                var plcEndpoint = FdcEndpointMapper.ToPlcEndpoint(ep);
                var device = new OpcUaDeviceInterface(ep.Id, plcEndpoint);   // 비 OPC-UA는 생성자가 거부 → catch로 스킵
                _plant.RegisterMachine(new Machine(ep.Id).AddInterface(device));
                devices.Add(device);

                if (!_equipmentMachines.TryGetValue(ep.EquipmentId, out var names))
                    _equipmentMachines[ep.EquipmentId] = names = new List<string>();
                names.Add(ep.Id);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FDC endpoint '{Endpoint}' setup skipped", ep.Id);
            }
        }

        if (devices.Count == 0)
        {
            _logger.LogInformation("No active OPC-UA FDC endpoints to collect.");
            return;
        }

        await _plant.InitializeAsync(stoppingToken);
        await _plant.StartAsync(stoppingToken);

        foreach (var device in devices)
        {
            var ep = endpoints.First(e => e.Id == device.InterfaceName);
            var parameters = await paramRepo.GetByEquipmentAsync(ep.EquipmentId, stoppingToken);
            var subscription = FdcEndpointMapper.ToSubscription(ep, parameters);
            await collector.StartCollectingAsync(device, new[] { subscription }, stoppingToken);
        }

        _logger.LogInformation("FDC collector started for {Count} endpoint(s).", devices.Count);
    }

    private void OnInterlockTriggered(object? sender, FdcInterlockTriggeredEventArgs e) =>
        _ = HandleInterlockAsync(e);

    private void OnInterlockResolved(object? sender, FdcInterlockResolvedEventArgs e) =>
        _ = HandleResolvedAsync(e);

    private async Task HandleResolvedAsync(FdcInterlockResolvedEventArgs e)
    {
        try
        {
            // 정상 복귀 통지 — 설비 재가동은 운영자 판단(자동 재시작은 하지 않음)
            await _notifier.NotifyEquipmentStateChangedAsync(e.EquipmentId, "InterlockCleared");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interlock resolve notification failed for {Equipment}/{Parameter}",
                e.EquipmentId, e.ParameterId);
        }
    }

    private void OnAlarmRaised(object? sender, FdcAlarmRaisedEventArgs e) =>
        _ = NotifySafelyAsync(() => _notifier.NotifyFdcDataReceivedAsync(
            e.EquipmentId, e.ParameterId, e.Value, isOutOfSpec: true), e.EquipmentId, e.ParameterId);

    private void OnAlarmCleared(object? sender, FdcAlarmClearedEventArgs e) =>
        _ = NotifySafelyAsync(() => _notifier.NotifyEquipmentStateChangedAsync(
            e.EquipmentId, "AlarmCleared"), e.EquipmentId, e.ParameterId);

    private async Task NotifySafelyAsync(Func<Task> notify, string equipmentId, string parameterId)
    {
        try { await notify(); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "FDC alarm notification failed for {Equipment}/{Parameter}", equipmentId, parameterId);
        }
    }

    private async Task HandleInterlockAsync(FdcInterlockTriggeredEventArgs e)
    {
        try
        {
            await _notifier.NotifyInterlockTriggeredAsync(
                e.EquipmentId, e.ParameterId, e.Result.Action, e.Result.Message);

            // STOP 액션이면 해당 설비의 모든 엔드포인트 Machine을 정지
            if (string.Equals(e.Result.Action, "STOP", StringComparison.OrdinalIgnoreCase)
                && _equipmentMachines.TryGetValue(e.EquipmentId, out var names))
            {
                foreach (var name in names)
                {
                    var machine = _plant.GetMachine(name);
                    if (machine is not null)
                        await machine.StopAsync();
                }
                await _notifier.NotifyEquipmentStateChangedAsync(e.EquipmentId, "Stopped");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Interlock handling failed for {Equipment}/{Parameter}",
                e.EquipmentId, e.ParameterId);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _plant.StopAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // 미기동·비정상 상태에서의 정지 실패는 종료 흐름을 막지 않는다
            _logger.LogWarning(ex, "FDC collector stop encountered an error");
        }

        // PlantController는 싱글톤이므로 DisposeAsync는 DI 컨테이너가 앱 종료 시 수행한다(여기서 호출하지 않음)
        _scope?.Dispose();
        await base.StopAsync(cancellationToken);
    }
}
