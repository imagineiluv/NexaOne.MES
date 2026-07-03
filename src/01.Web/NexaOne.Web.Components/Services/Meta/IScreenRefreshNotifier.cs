namespace NexaOne.Web.Services.Meta;

/// <summary>화면 실시간 재조회 알림 포트(Phase-2 실시간 v3 — 폴링→푸시 정밀화). 도메인 이벤트가 발행되면
/// 구독 화면(MetaScreen, RefreshIntervalSeconds>0인 라이브 화면)이 폴링 주기를 기다리지 않고 즉시 재조회한다.
/// 구현은 호스트 소유(인메모리 이벤트 버스 브리징 — Blazor Server 회로는 호스트와 동일 프로세스라 SignalR
/// 클라이언트 없이 직접 구독한다). 미등록 환경(modules OFF·bUnit)에서는 폴링만 동작(화면이 관용 처리).</summary>
public interface IScreenRefreshNotifier
{
    /// <summary>알림 구독 — 반환 IDisposable 폐기로 해지한다. 콜백은 버스 스레드에서 호출될 수 있다.</summary>
    IDisposable Subscribe(Func<Task> onChanged);
}
