namespace NexaOne.Server.Gateway;

// 기존 NexaOne.API와 동일 JSON 계약(필드명/형태 일치). 호스트가 API 웹앱을 참조하지 않도록 로컬 정의한다.
public record LoginRequest(string UserId, string Password, string PlantId = "DEFAULT");

public record LoginResponse(
    string AccessToken,
    string RefreshToken,
    string UserId,
    string UserName,
    string PlantId,
    IReadOnlyList<string> Roles,
    bool RequirePasswordChange = false);

public record RefreshRequest(string UserId, string RefreshToken);

public record TokenRefreshResponse(string AccessToken, string RefreshToken);

/// <summary>로그인 실패 사유 코드(SYS_LOGIN_FAILURE_HIST.FAILURE_REASON 값) — 호스트 로컬 정의.
/// NexaOne.SYS는 플러그인(ReferenceOutputAssembly=false)이라 컴파일 타임에 타입이 보이지 않으므로,
/// NexaOne.SYS.Domain.LoginFailureHistory.Reasons와 동일한 문자열 상수를 여기서 미러링한다(값 일치 필수).</summary>
internal static class LoginFailureReasons
{
    public const string UserNotFound = "UserNotFound";
    public const string WrongPassword = "WrongPassword";
    public const string InactiveUser = "InactiveUser";
    public const string AccountLocked = "AccountLocked";
}
