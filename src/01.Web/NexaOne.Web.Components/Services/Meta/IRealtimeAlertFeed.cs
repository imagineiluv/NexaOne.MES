namespace NexaOne.Web.Services.Meta;

/// <summary>실시간 알림 1건 — 도메인 이벤트(알람/인터락/작업지시)를 셸 알림 센터가 표시하는 형태.</summary>
public sealed record RealtimeAlert(DateTime OccurredAt, string Category, string Title);

/// <summary>
/// 셸 알림 센터 포트(실시간 v3 <see cref="IScreenRefreshNotifier"/>와 동일 패턴) — 호스트가 이벤트 버스에서
/// 알림성 이벤트를 밀어 넣고, 셸(벨 아이콘)이 구독해 뱃지/목록을 갱신한다. 미등록 환경(테스트 등)에서는
/// 셸이 벨을 렌더하지 않는다.
/// </summary>
public interface IRealtimeAlertFeed
{
    /// <summary>최근 알림 스냅숏(최신순, 유계 — 호스트 구현 50건).</summary>
    IReadOnlyList<RealtimeAlert> Recent { get; }

    /// <summary>새 알림 구독 — 반환 IDisposable로 해지. 콜백은 임의 스레드에서 호출될 수 있다(구독자가 InvokeAsync 전환).</summary>
    IDisposable Subscribe(Func<RealtimeAlert, Task> callback);
}
