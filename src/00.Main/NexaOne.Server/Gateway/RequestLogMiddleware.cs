using System.Diagnostics;
using System.Security.Claims;
using NexaOne.Application.Messaging;

namespace NexaOne.Server.Gateway;

/// <summary>API 요청 로그 미들웨어(호스트 인프라) — /api/* 요청을 SYS_REQUEST_LOG(V062)에 1행씩 기록한다.
/// SYSTEM2 요청 로그 뷰어(REQLOG) 화면의 데이터 원천. 기본 OFF — RequestLogging:Enabled=true로만 켠다
/// (테스트/CI 무영향, RefreshTokenCleanupWorker 게이트 관례). 기록은 게이트웨이와 동일한 IRuleDispatcher
/// (provider-agnostic)로 수행하고, 기록 실패는 잡아 삼켜 요청 처리를 절대 막지 않는다.</summary>
public sealed class RequestLogMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IRuleDispatcher _dispatcher;

    private const string InsertSql = @"
        INSERT INTO SYS_REQUEST_LOG (LOG_ID, METHOD, PATH, STATUS_CODE, ELAPSED_MS, USER_ID, CLIENT_IP, REQUESTED_AT)
        VALUES (@id, @method, @path, @status, @elapsed, @userId, @clientIp, @at)";

    public RequestLogMiddleware(RequestDelegate next, IRuleDispatcher dispatcher)
    {
        _next = next;
        _dispatcher = dispatcher;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // API 경로만 기록(정적/허브/헬스 제외) — 로그 폭주 방지.
        if (!context.Request.Path.StartsWithSegments("/api"))
        {
            await _next(context);
            return;
        }

        var startedAt = DateTime.UtcNow;
        var sw = Stopwatch.StartNew();
        await _next(context);
        sw.Stop();

        try
        {
            await _dispatcher.ExecuteAsync(InsertSql, new Dictionary<string, object>
            {
                ["id"] = Guid.NewGuid().ToString("N"),
                ["method"] = context.Request.Method,
                ["path"] = context.Request.Path.Value ?? string.Empty,
                ["status"] = context.Response.StatusCode,
                ["elapsed"] = (int)sw.ElapsedMilliseconds,
                ["userId"] = (object?)context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? DBNull.Value,
                ["clientIp"] = (object?)context.Connection.RemoteIpAddress?.ToString() ?? DBNull.Value,
                ["at"] = startedAt.ToString("yyyy-MM-dd HH:mm:ss"),
            }, context.RequestAborted);
        }
        catch
        {
            // 로그 기록 실패는 요청 실패로 승격하지 않는다(관측 실패 ≠ 업무 실패).
        }
    }
}
