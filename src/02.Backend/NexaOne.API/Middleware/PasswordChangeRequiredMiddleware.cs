using NexaOne.API.Services;

namespace NexaOne.API.Middleware;

/// <summary>§20.10 — 임시 비밀번호(Forgot 등)로 로그인한 사용자는 비밀번호 변경 완료 전까지
/// 메뉴 로딩과 업무 API 호출을 차단한다. 판정은 토큰의 pwdChange 클레임으로만 하며(요청당 DB 조회 없음),
/// 변경 성공 시 클레임 없는 새 토큰이 재발급되므로 이전 토큰은 만료까지 차단 상태로 남는다.</summary>
public sealed class PasswordChangeRequiredMiddleware
{
    private readonly RequestDelegate _next;

    public PasswordChangeRequiredMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var requiresChange = context.User?.FindFirst(JwtService.PasswordChangeClaim)?.Value == "true";
        if (requiresChange && !IsAllowedPath(context.Request.Path))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new
            {
                code = "PASSWORD_CHANGE_REQUIRED",
                message = "비밀번호 변경 후 이용할 수 있습니다."
            });
            return;
        }

        await _next(context);
    }

    // 인증·비밀번호 변경 경로만 허용 — 메뉴/업무 API와 실시간 허브는 차단 (§20.10)
    private static bool IsAllowedPath(PathString path)
        => path.StartsWithSegments("/api/v1/auth")
           || path.StartsWithSegments("/health");
}
