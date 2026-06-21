using NexaOne.Application.Auth;

namespace NexaOne.Server.Gateway;

/// <summary>비밀번호 강제 변경(pwdChange 클레임) 사용자의 업무 데이터 호출을 차단한다(§20.10). 통합 호스트는 UI를
/// 같은 프로세스에서 서빙하므로 데이터 표면(/api/v1/* 비-auth + /hubs/*)만 403으로 막고, 정적 SPA·Blazor 셸·
/// /health·/diag는 허용해 강제변경 사용자가 앱을 로드해 비밀번호를 바꿀 수 있게 한다. 판정은 토큰 클레임만(DB 무조회).</summary>
public sealed class PasswordChangeRequiredMiddleware
{
    private readonly RequestDelegate _next;
    public PasswordChangeRequiredMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var requiresChange = context.User?.FindFirst(JwtService.PasswordChangeClaim)?.Value == "true";
        if (requiresChange && IsBlocked(context.Request.Path))
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

    // 차단 = 업무 데이터 표면만: /api/v1/* (단 /api/v1/auth는 허용) 또는 /hubs/*. 정적 UI·/health·/diag는 통과.
    private static bool IsBlocked(PathString path)
        => (path.StartsWithSegments("/api/v1") && !path.StartsWithSegments("/api/v1/auth"))
           || path.StartsWithSegments("/hubs");
}
