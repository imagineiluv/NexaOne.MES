namespace NexaOne.API.Services;

/// <summary>도메인 이벤트 → SignalR 알림의 단일 경로 조정(ADR-002 §2.5). 이벤트 버스가 활성
/// (Events:Outbox:Enabled)이면 디스패처/구독자가 이벤트 기반 알림을 전달하므로 컨트롤러는 '이벤트로 뒷받침되는'
/// 직접 SignalR 호출을 생략해 이중 발행을 막는다; 비활성이면 컨트롤러가 즉시 직접 발행한다(폴백, 무지연).
/// 대응 이벤트가 없는 알림(고빈도 FDC 수집·인터록 발동)은 버스 상태와 무관하게 항상 직접 발행한다.</summary>
public sealed class RealtimeNotificationCoordinator
{
    public RealtimeNotificationCoordinator(bool busDeliversEvents) => BusDeliversEvents = busDeliversEvents;

    /// <summary>이벤트 버스가 도메인 이벤트 기반 알림을 전달하는가(=Events:Outbox:Enabled). true면 컨트롤러는
    /// 이벤트로 뒷받침되는 알림을 직접 발행하지 않는다(구독자가 전달).</summary>
    public bool BusDeliversEvents { get; }
}
