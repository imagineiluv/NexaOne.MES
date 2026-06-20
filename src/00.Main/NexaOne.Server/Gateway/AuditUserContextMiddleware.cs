using System.Security.Claims;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.Server.Gateway;

/// <summary>요청 단위 감사 사용자 앰비언트 설정 — JWT 주체(NameIdentifier/sub)를 CurrentUserContext.UserId(AsyncLocal)에
/// 싣고 요청 종료 시 복원한다. 모듈 리포지토리·ServiceObjectProcessor가 이 값을 감사 컬럼(@currentUser)으로 읽는다.
/// 비인증이면 null로 두어 "SYSTEM" 폴백. UseAuthentication 다음에 배치해야 User 클레임이 채워져 있다.</summary>
public sealed class AuditUserContextMiddleware
{
    private readonly RequestDelegate _next;
    public AuditUserContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var authUser = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User?.FindFirst("sub")?.Value;
        var previous = CurrentUserContext.UserId;
        CurrentUserContext.UserId = authUser;
        try { await _next(context); }
        finally { CurrentUserContext.UserId = previous; }
    }
}
