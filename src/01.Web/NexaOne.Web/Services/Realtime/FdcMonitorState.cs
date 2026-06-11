namespace NexaOne.Web.Services.Realtime;

/// <summary>
/// FDC 실시간 모니터의 화면 상태 (설계서 §20.9 탭 비활성화 조항의 웹 적응).
/// 웹 MDI에서 탭 전환은 페이지 dispose이므로 WinForms처럼 구독을 유지할 수 없다.
/// 대신 조회 조건·실시간 감시 여부·인터록 이력을 서킷 수명(Scoped) 상태로 보존해,
/// 탭 재활성화 시 감시를 자동 재개하고 전체 재조회로 '누적분 일괄 반영'을 대체한다.
/// (탭에서 벗어나 있는 동안의 수집값·인터록 이벤트는 서버에 저장되며, 인터록 화면 로그만
/// 수신 시점에 페이지가 없으면 기록되지 않는다 — 정식 이력은 알람 화면에서 조회한다.)
/// </summary>
public sealed class FdcMonitorState
{
    public string ParameterId { get; set; } = string.Empty;
    public int Limit { get; set; } = 50;

    /// <summary>실시간 감시 토글 — 탭 전환 후 복귀 시에도 유지되어 감시가 자동 재개된다 (§20.9).</summary>
    public bool IsWatching { get; set; }

    public List<InterlockLogEntry> InterlockLog { get; } = [];

    public sealed record InterlockLogEntry(DateTime Time, string Action, string Message);
}
