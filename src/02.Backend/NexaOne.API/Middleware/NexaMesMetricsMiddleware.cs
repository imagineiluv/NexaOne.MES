using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Routing;
using NexaOne.Common.Telemetry;

namespace NexaOne.API.Middleware;

/// <summary>
/// §17.5 — API 요청 시간(<c>nexames_api_request_duration_ms</c>)과 오류(<c>nexames_api_error_rate</c>)를
/// 계측하고, 활동 사용자(<c>nexames_active_users</c>)를 갱신한다. 라우트 라벨은 카디널리티 폭증을 막기 위해
/// 원시 경로가 아닌 <b>라우트 템플릿</b>을 사용하고, 매칭되지 않은 요청은 <c>unknown</c>으로 집계한다.
/// </summary>
/// <remarks>
/// 예외 처리기(<c>UseExceptionHandler</c>)보다 앞에 배치해, 예외가 500으로 변환된 최종 상태코드와 전체
/// 처리 시간을 <c>finally</c>에서 관측한다. plantId/userId 클레임은 인증이 끝난 뒤 채워지므로 역시 finally에서 읽는다.
/// </remarks>
public sealed class NexaMesMetricsMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ActiveUserTracker _activeUsers;

    public NexaMesMetricsMiddleware(RequestDelegate next, ActiveUserTracker activeUsers)
    {
        _next = next;
        _activeUsers = activeUsers;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var startTimestamp = Stopwatch.GetTimestamp();
        try
        {
            await _next(context);
        }
        finally
        {
            var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            var plantId = context.User?.FindFirst("plantId")?.Value ?? "GLOBAL";
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText
                        ?? "unknown";   // 미매칭(404 등)은 원시 경로 대신 unknown (카디널리티 방어)
            var method = context.Request.Method;
            var status = context.Response.StatusCode;

            var durationTags = new TagList
            {
                { "route", route },
                { "method", method },
                { "plantId", plantId },
                { "resultCode", status },
            };
            NexaMesMetrics.ApiRequestDuration.Record(elapsedMs, durationTags);

            if (status >= 400)
            {
                var errorTags = new TagList
                {
                    { "route", route },
                    { "errorCode", status },
                    { "plantId", plantId },
                };
                NexaMesMetrics.ApiErrors.Add(1, errorTags);
            }

            var userId = context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value
                ?? context.User?.FindFirst("sub")?.Value;
            _activeUsers.Touch(plantId, userId);
        }
    }
}
