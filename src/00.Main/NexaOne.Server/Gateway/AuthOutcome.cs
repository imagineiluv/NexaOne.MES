using Microsoft.AspNetCore.Mvc;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>로그인/리프레시 서비스 결과 — 컨트롤러가 200/401로 매핑한다(기존 API와 동일 상태코드/오류 코드).
/// 호스트에 별도 인증 애플리케이션 계층이 없어 서비스가 IActionResult를 직접 반환한다(허용된 트레이드오프; 알려진 부채).</summary>
public sealed class AuthOutcome
{
    private AuthOutcome(IActionResult result) => Result = result;
    public IActionResult Result { get; }

    public static AuthOutcome Ok(object body) => new(new OkObjectResult(body));

    public static AuthOutcome InvalidCredentials() =>
        new(new UnauthorizedObjectResult(new Error("INVALID_CREDENTIALS", "Invalid credentials.")));

    public static AuthOutcome AccountLocked(DateTime lockedUntil, DateTime now)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling((lockedUntil - now).TotalMinutes));
        return new(new UnauthorizedObjectResult(new Error("ACCOUNT_LOCKED",
            $"비밀번호 5회 연속 오류로 계정이 잠겼습니다. 약 {minutes}분 후 다시 시도하거나 관리자에게 문의하세요.")));
    }

    public static AuthOutcome InvalidRefreshToken() =>
        new(new UnauthorizedObjectResult(new Error("Auth.InvalidRefreshToken", "Invalid or expired refresh token.")));
}
