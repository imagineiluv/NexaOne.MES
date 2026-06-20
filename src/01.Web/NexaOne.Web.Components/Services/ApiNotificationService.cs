namespace NexaOne.Web.Services;

/// <summary>전역 API 통지 심각도.</summary>
public enum ApiNotificationSeverity { Error, Warning, Info }

/// <summary>화면 상단 토스트로 표시할 통지 1건.</summary>
public sealed record ApiNotification(string Message, ApiNotificationSeverity Severity);

/// <summary>
/// API 호출 실패(권한 거부 403·서버 오류 5xx 등)를 화면 상단 토스트로 surfacing하기 위한
/// 서킷 수명 통지 채널. <see cref="Api.ApiClient"/>가 발신하고 전역 컴포넌트(ApiToastHost)가 구독한다.
/// </summary>
/// <remarks>
/// 페이지가 자체적으로 오류를 표시하는 경로(PostWithError/PatchWithError 등)는 발신하지 않아
/// 중복 노출을 피한다 — 아무도 표시하지 않는 403/5xx만 이 채널로 전역 노출한다.
/// </remarks>
public sealed class ApiNotificationService
{
    /// <summary>통지 발생 이벤트. 백그라운드 스레드에서 호출될 수 있으므로 구독자는 InvokeAsync로 UI 전환할 것.</summary>
    public event Action<ApiNotification>? OnNotify;

    public void Notify(string message, ApiNotificationSeverity severity = ApiNotificationSeverity.Error)
        => OnNotify?.Invoke(new ApiNotification(message, severity));
}
