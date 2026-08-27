using Microsoft.Extensions.Hosting;
using System.Text.Json;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;
using NexaOne.FDC.Infrastructure.Equipment;
using NexaOne.Infrastructure.Messaging;
using NexaFramework;

namespace NexaOne.FDC.Infrastructure.Equipment;

/// <summary>
/// FDC 실시간 수집 워커 — 모듈 소유 오케스트레이션(ADR-006 Phase 2).
/// API 측 FdcCollectorHostedService의 수집 오케스트레이션을 미러링하되, best-effort 실시간 통지는 SignalR 직접 호출
/// 대신 <see cref="IMessageBus"/>(server.xml의 messageBus 빈)로 도메인 이벤트를 발행한다. 버스 구독자가
/// 이를 받아 SignalR로 전달한다(ADR-002 §2.5 — 실시간 알림을 '버스 소비'로 일원화).
/// 인터락 액션은 프로젝트별 정책을 위한 불투명 payload이며, 해석과 설비 제어는 플러그인/소비자가 담당한다.
/// </summary>
/// <remarks>
/// API/웹 타입(IEesHubNotifier·EquipmentChannelStatusRegistry 등)을 일절 참조하지 않는다 — 모듈 경계 보존.
/// 채널 상태 메트릭(§17.5)은 본 워커 범위에서 생략한다(웹 타입 의존 금지가 우선).
/// 실제 PLC 서버 연결을 시도하므로 <c>_enabled = false</c>면 즉시 no-op로 반환한다(연결 시도 안 함).
/// PlantController/FdcPlcDeviceFactory/IMessageBus는 Spring 부모/모듈 컨텍스트의 빈을 호스트가 주입한다.
/// 본 워커의 이벤트 구독은 관제/UI 알림 전용이다. 발행은 fire-and-forget이므로 물리 안전 보장을 제공하지 않으며,
/// 실제 인터락 동작은 프로젝트가 별도 fail-safe action plugin으로 구현하고 HIL로 검증해야 한다.
/// 본 워커는 수집 연결의 시작/종료 수명 주기만 관리하며 인터락에 따른 Machine 정지나 설비 상태 확정을 하지 않는다.
/// </remarks>
public sealed class FdcCollectionWorker : BackgroundService
{
    private readonly FdcCollectorService _collector;
    private readonly IFdcEquipmentEndpointRepository _endpointRepo;
    private readonly IFdcParameterRepository _paramRepo;
    private readonly PlantController _plant;       // server.xml plantController 빈 — PLC 수집 수명 주기만 제어
    private readonly FdcPlcDeviceFactory _deviceFactory;
    private readonly IMessageBus _bus;             // server.xml messageBus 빈 — 도메인 이벤트 발행 백본
    private readonly bool _enabled;                // Worker:Fdc:Enabled 게이트(기본 false)
    private readonly string _topic;                // 발행 토픽(기본 "nexaone.events")

    public FdcCollectionWorker(
        FdcCollectorService collector,
        IFdcEquipmentEndpointRepository endpointRepo,
        IFdcParameterRepository paramRepo,
        PlantController plant,
        FdcPlcDeviceFactory deviceFactory,
        IMessageBus bus,
        bool enabled,
        string topic)
    {
        _collector = collector;
        _endpointRepo = endpointRepo;
        _paramRepo = paramRepo;
        _plant = plant;
        _deviceFactory = deviceFactory;
        _bus = bus;
        _enabled = enabled;
        _topic = topic;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 활성 플래그가 false면 즉시 no-op — PLC 연결을 시도하지 않는다(개발/CI에서 연결 실패 Abort 방지).
        if (!_enabled)
            return;

        _collector.InterlockTriggered += OnInterlockTriggered;
        _collector.InterlockResolved += OnInterlockResolved;
        _collector.AlarmRaised += OnAlarmRaised;
        _collector.AlarmCleared += OnAlarmCleared;

        var endpoints = await _endpointRepo.GetAllActiveAsync(stoppingToken);
        var devices = new List<(FdcEquipmentEndpoint Endpoint, PlcDeviceInterface Device)>();

        foreach (var ep in endpoints)
        {
            // 구성 오류(미지원 프로토콜·미등록 드라이버·중복 Machine)는 호스트 로그에 원인이
            // 남도록 예외를 전파한다. 엔드포인트를 조용히 건너뛰면 수집 누락을 탐지할 수 없다.
            var device = _deviceFactory.Create(ep);
            _plant.RegisterMachine(new Machine(ep.Id).AddInterface(device));
            devices.Add((ep, device));
        }

        if (devices.Count == 0)
            return;

        await _plant.InitializeAsync(stoppingToken);
        await _plant.StartAsync(stoppingToken);

        foreach (var registration in devices)
        {
            var ep = registration.Endpoint;
            var parameters = await _paramRepo.GetByEquipmentAsync(ep.EquipmentId, stoppingToken);
            var subscription = FdcEndpointMapper.ToSubscription(ep, parameters);
            await registration.Device.SubscribeAsync(
                new[] { subscription },
                sample => _collector.OnTagChangeAsync(
                    ep.EquipmentId,
                    sample,
                    stoppingToken),
                stoppingToken);
        }
    }

    // ── 이벤트 → IMessageBus 발행. EventType/Payload는 RealtimeNotificationDispatch가 인식하는 값과 일치시킨다. ──

    private void OnInterlockTriggered(object? sender, FdcInterlockTriggeredEventArgs e) =>
        _ = PublishInterlockNotificationAsync(e);

    private void OnInterlockResolved(object? sender, FdcInterlockResolvedEventArgs e) =>
        // 정상 복귀는 인터락 사실만 통지한다. 실제 설비 상태는 EST/driver readback 소유다.
        _ = PublishSafelyAsync(
            "InterlockResolved",
            e.EquipmentId,
            JsonSerializer.Serialize(new
            {
                e.EffectId,
                e.RuleId,
                e.ParameterId,
                e.Value,
                e.ResolvedAt,
            }));

    private void OnAlarmRaised(object? sender, FdcAlarmRaisedEventArgs e) =>
        _ = PublishSafelyAsync("FdcAlarmRaised", e.EquipmentId, e.Value.ToString());

    private void OnAlarmCleared(object? sender, FdcAlarmClearedEventArgs e) =>
        _ = PublishSafelyAsync("FdcAlarmCleared", e.EquipmentId, "AlarmCleared");

    private async Task PublishInterlockNotificationAsync(FdcInterlockTriggeredEventArgs e)
    {
        // Action은 공통 FDC가 해석하지 않는 프로젝트 정책 payload다. 플러그인/소비자가 실제 정지 여부와
        // 설비 상태 전이를 결정하며, 워커는 정규화된 인터락 발동 사실만 발행한다.
        await PublishSafelyAsync(
            "InterlockTriggered",
            e.EquipmentId,
            JsonSerializer.Serialize(new
            {
                e.EffectId,
                e.Result.RuleId,
                e.ParameterId,
                e.Result.Action,
                e.Result.Message,
                e.Value,
            }));
    }

    /// <summary>관제/UI용 best-effort 버스 알림 1건. EffectId가 direct/outbox 중복 전달의 멱등 키다.
    /// 물리 인터락 action의 성공 여부를 나타내지 않는다.</summary>
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
