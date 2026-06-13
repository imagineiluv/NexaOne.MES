using System.Security.Claims;
using Serilog.Context;

namespace NexaOne.API.Middleware;

/// <summary>§17.3 — 요청 단위 구조화 로그 공통 필드 주입. 외부 Header X-Correlation-Id를
/// 수신하고 없으면 서버가 생성해 응답 Header로 되돌려준다. 같은 요청에서 발생하는 모든 로그
/// (감사 로그 포함)는 CorrelationId/UserId/PlantId를 공유한다. UiId/MenuId/RuleId/QueryId 등
/// 화면 메타데이터 필드는 해당 개념이 도입될 때 같은 방식으로 추가한다.</summary>
public sealed class RequestLogContextMiddleware
{
    public const string CorrelationHeader = "X-Correlation-Id";

    private readonly RequestDelegate _next;

    public RequestLogContextMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        // 클라이언트 제공 값은 응답 Header와 모든 로그에 그대로 실리므로
        // 길이·문자 집합을 검증하고, 통과하지 못하면 서버가 새로 생성한다 (로그 오염 방어)
        var correlationId = context.Request.Headers[CorrelationHeader].FirstOrDefault();
        if (!IsValidCorrelationId(correlationId))
            correlationId = Guid.NewGuid().ToString("N");
        context.Response.Headers[CorrelationHeader] = correlationId;

        // 비로그인 요청은 anonymous, Plant 없는 System 요청은 GLOBAL (§17.3)
        var authUser = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? context.User?.FindFirst("sub")?.Value;
        var userId = authUser ?? "anonymous";
        var plantId = context.User?.FindFirst("plantId")?.Value ?? "GLOBAL";

        // 감사 컬럼(CREATED_BY/UPDATED_BY)에 실제 사용자가 실리도록 요청 앰비언트에 설정한다.
        // 비인증이면 null로 두어 ServiceObjectProcessor가 "SYSTEM"으로 폴백하게 한다.
        var previousAuditUser = NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId;
        NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = authUser;
        try
        {
            using (LogContext.PushProperty("CorrelationId", correlationId))
            using (LogContext.PushProperty("UserId", userId))
            using (LogContext.PushProperty("PlantId", plantId))
            {
                await _next(context);
            }
        }
        finally
        {
            NexaOne.Infrastructure.Persistence.CurrentUserContext.UserId = previousAuditUser;
        }
    }

    private static bool IsValidCorrelationId(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 64
           && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');
}
