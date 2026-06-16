using Microsoft.Extensions.Hosting;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaOne.Infrastructure.Messaging;
using NexusFramework;
using NexusLogic.Plc.Abstractions.Interfaces;

namespace NexaOne.FDC.Application.Fdc;

/// <summary>
/// FDC 실시간 수집 워커 — 모듈 소유 오케스트레이션(ADR-006 Phase 2).
/// API 측 FdcCollectorHostedService의 수집 오케스트레이션을 미러링하되, 실시간 통지는 SignalR 직접 호출
/// 대신 <see cref="IMessageBus"/>(server.xml의 messageBus 빈)로 도메인 이벤트를 발행한다. 버스 구독자가
/// 이를 받아 SignalR로 전달한다(ADR-002 §2.5 — 실시간 알림을 '버스 소비'로 일원화).
/// </summary>
/// <remarks>
/// API/웹 타입(IEesHubNotifier·EquipmentChannelStatusRegistry 등)을 일절 참조하지 않는다 — 모듈 경계 보존.
/// 채널 상태 메트릭(§17.5)은 본 워커 범위에서 생략한다(웹 타입 의존 금지가 우선).
/// 실제 OPC-UA 서버 연결을 시도하므로 <c>_enabled = false</c>면 즉시 no-op로 반환한다(연결 시도 안 함).
/// PlantController/IPlcDriver/IMessageBus는 server.xml 부모 컨텍스트의 공통 서버 빈을 호스트가 주입한다.
/// </remarks>
public sealed class FdcCollectionWorker : BackgroundService
{
    private readonly FdcCollectorService _collector;
    private readonly IFdcEquipmentEndpointRepository _endpointRepo;
    private readonly IFdcParameterRepository _paramRepo;
    private readonly PlantController _plant;       // server.xml plantController 빈 — FdcCollectionWorker가 수동 제어
    private readonly IPlcDriver _driver;           // server.xml opcUaDriver 빈 — 모듈은 호출만(설계: 드라이버는 서버 빈)
    private readonly IMessageBus _bus;             // server.xml messageBus 빈 — 도메인 이벤트 발행 백본
    private readonly bool _enabled;                // Worker:Fdc:Enabled 게이트(기본 false)
    private readonly string _topic;                // 발행 토픽(기본 "nexaone.events")

    private readonly Dictionary<string, List<string>> _equipmentMachines = new();  // equipmentId -> machine(endpoint) names

    public FdcCollectionWorker(
        FdcCollectorService collector,
        IFdcEquipmentEndpointRepository endpointRepo,
        IFdcParameterRepository paramRepo,
        PlantController plant,
        IPlcDriver driver,
        IMessageBus bus,
        bool enabled,
        string topic)
    {
        _collector = collector;
        _endpointRepo = endpointRepo;
        _paramRepo = paramRepo;
        _plant = plant;
        _driver = driver;
        _bus = bus;
        _enabled = enabled;
        _topic = topic;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 활성 플래그가 false면 즉시 no-op — OPC-UA 연결을 시도하지 않는다(개발/CI에서 연결 실패 Abort 방지).
        if (!_enabled)
            return;

        _collector.InterlockTriggered += OnInterlockTriggered;
        _collector.InterlockResolved += OnInterlockResolved;
        _collector.AlarmRaised += OnAlarmRaised;
        _collector.AlarmCleared += OnAlarmCleared;

        var endpoints = await _endpointRepo.GetAllActiveAsync(stoppingToken);
        var devices = new List<OpcUaDeviceInterface>();

        foreach (var ep in endpoints)
        {
            try
            {
                var plcEndpoint = FdcEndpointMapper.ToPlcEndpoint(ep);
                // 3번째 인자로 주입받은 드라이버(opcUaDriver)를 넘긴다 — 기본 new OpcUaDriver() 대신.
                var device = new OpcUaDeviceInterface(ep.Id, plcEndpoint, _driver);   // 비 OPC-UA는 생성자가 거부 → catch로 스킵
                _plant.RegisterMachine(new Machine(ep.Id).AddInterface(device));
                devices.Add(device);

                if (!_equipmentMachines.TryGetValue(ep.EquipmentId, out var names))
                    _equipmentMachines[ep.EquipmentId] = names = new List<string>();
                names.Add(ep.Id);
            }
            catch
            {
                // 엔드포인트 1건 설정 실패는 전체 수집을 막지 않는다(비 OPC-UA·매핑 실패 등은 스킵).
            }
        }

        if (devices.Count == 0)
            return;

        await _plant.InitializeAsync(stoppingToken);
        await _plant.StartAsync(stoppingToken);

        foreach (var device in devices)
        {
            var ep = endpoints.First(e => e.Id == device.InterfaceName);
            var parameters = await _paramRepo.GetByEquipmentAsync(ep.EquipmentId, stoppingToken);
            var subscription = FdcEndpointMapper.ToSubscription(ep, parameters);
            await _collector.StartCollectingAsync(device, new[] { subscription }, stoppingToken);
        }
    }

    // ── 이벤트 → IMessageBus 발행. EventType/Payload는 RealtimeNotificationDispatch가 인식하는 값과 일치시킨다. ──

    private void OnInterlockTriggered(object? sender, FdcInterlockTriggeredEventArgs e) =>
        _ = HandleInterlockAsync(e);

    private void OnInterlockResolved(object? sender, FdcInterlockResolvedEventArgs e) =>
        // 정상 복귀 통지 — 설비 재가동은 운영자 판단(자동 재시작은 하지 않음).
        _ = PublishSafelyAsync("EquipmentStateChanged", e.EquipmentId, "InterlockCleared");

    private void OnAlarmRaised(object? sender, FdcAlarmRaisedEventArgs e) =>
        _ = PublishSafelyAsync("FdcAlarmRaised", e.EquipmentId, e.Value.ToString());

    private void OnAlarmCleared(object? sender, FdcAlarmClearedEventArgs e) =>
        _ = PublishSafelyAsync("FdcAlarmCleared", e.EquipmentId, "AlarmCleared");

    private async Task HandleInterlockAsync(FdcInterlockTriggeredEventArgs e)
    {
        // 인터락 발동 통지(payload=Action). RealtimeNotificationDispatch의 "InterlockTriggered" 매핑이 상태 변경으로 전달.
        await PublishSafelyAsync("InterlockTriggered", e.EquipmentId, e.Result.Action);

        // STOP 액션이면 해당 설비의 모든 엔드포인트 Machine을 정지하고 상태 변경을 통지한다.
        if (string.Equals(e.Result.Action, "STOP", StringComparison.OrdinalIgnoreCase)
            && _equipmentMachines.TryGetValue(e.EquipmentId, out var names))
        {
            try
            {
                foreach (var name in names)
                {
                    var machine = _plant.GetMachine(name);
                    if (machine is not null)
                        await machine.StopAsync();
                }
            }
            catch
            {
                // 정지 실패는 통지 흐름을 막지 않는다(이미 정지/비정상 상태 등).
            }
            await PublishSafelyAsync("EquipmentStateChanged", e.EquipmentId, "Stopped");
        }
    }

    /// <summary>버스 발행 1건. 발행 실패가 수집 루프/다른 이벤트 처리를 막지 않도록 예외를 삼킨다.</summary>
    private async Task PublishSafelyAsync(string eventType, string aggregateId, string payload)
    {
        try
        {
            var message = DomainEventMessage.Create(eventType, module: "FDC", aggregateId: aggregateId, payload: payload);
            await _bus.PublishAsync(_topic, message);
        }
        catch
        {
            // 통지 실패는 수집을 멈추지 않는다(버스 미가용·구독자 예외 등).
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _plant.StopAsync(cancellationToken);
        }
        catch
        {
            // 미기동·비정상 상태에서의 정지 실패는 종료 흐름을 막지 않는다.
        }
        await base.StopAsync(cancellationToken);
    }
}
