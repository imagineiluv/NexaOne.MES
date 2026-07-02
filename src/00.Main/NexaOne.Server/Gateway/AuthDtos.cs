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

public record LogoutRequest(string RefreshToken);

public record ChangePasswordRequest(string CurrentPassword, string NewPassword, string ConfirmPassword);

// 관리자 사용자 등록. PlantId/Language는 선택(기본). RoleId는 필수(권한 합성 기반).
public record RegisterRequest(
    string UserId, string UserName, string Password, string Email, string RoleId, string Language = "KoKr");

public record CurrentUserResponse(string? UserId, string? UserName, string? PlantId, IReadOnlyList<string> Roles);

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

/// <summary>비밀번호 재설정 요청(사용자 열거 방지 — 응답은 존재 여부와 무관하게 동일).</summary>
public record ForgotPasswordRequest(string UserId);

/// <summary>재설정 토큰으로 새 비밀번호 설정.</summary>
public record ResetPasswordRequest(string Token, string NewPassword, string ConfirmPassword);
