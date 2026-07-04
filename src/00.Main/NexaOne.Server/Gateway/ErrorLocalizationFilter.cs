using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using NexaOne.Common;

namespace NexaOne.Server.Gateway;

/// <summary>서버 오류 메시지 다국어(P3-14) 응답 경계 — Error 본문을 요청 언어로 번역한다.
/// 브리지/게이트웨이가 반환하는 ObjectResult(Value=Error, BridgeResultExtensions 참조)를 가로채,
/// Error.MessageKey가 있고 요청 언어가 비-한국어면 Description을 리소스 번역으로 치환한다(상태 코드 불변).
/// 컨트롤러 호출부는 무변경 — 전역 필터 1곳으로 처리(MessageKey 없는 기존 Error는 그대로 통과).</summary>
public sealed class ErrorLocalizationFilter : IAsyncResultFilter
{
    private readonly IErrorLocalizer _localizer;

    public ErrorLocalizationFilter(IErrorLocalizer localizer) => _localizer = localizer;

    public async Task OnResultExecutionAsync(ResultExecutingContext context, ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: Error err } obj && err.MessageKey is not null)
        {
            var language = ResolveLanguage(context.HttpContext.Request.Headers.AcceptLanguage.ToString());
            var translated = _localizer.Translate(err.MessageKey, language, err.MessageArgs);
            if (!string.IsNullOrEmpty(translated))
                obj.Value = err with { Description = translated };
        }
        await next();
    }

    // Accept-Language → 내부 언어 코드. "en"으로 시작하면 EnUs, 그 외(빈 값/ko-*)는 KoKr(기본).
    // 클라이언트(ApiClient)는 사용자 언어를 "en-US"/"ko-KR"로 보낸다.
    private static string ResolveLanguage(string acceptLanguage)
        => acceptLanguage.TrimStart().StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "EnUs" : "KoKr";
}
