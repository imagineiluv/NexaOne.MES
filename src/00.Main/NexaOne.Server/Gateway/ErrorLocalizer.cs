using System.Collections.Concurrent;
using System.Globalization;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;

namespace NexaOne.Server.Gateway;

/// <summary>서버 오류 메시지 다국어(P3-14) — Error.MessageKey를 요청 언어의 리소스로 해석한다.
/// SYS_MULTI_LANGUAGE_RESOURCE(클라이언트 UI 다국어와 동일 테이블)를 언어별 1회 로드해 캐시한다
/// (리소스는 거의 불변이라 프로세스 수명 캐시로 충분 — 시드 변경 시 재기동으로 반영).</summary>
public interface IErrorLocalizer
{
    /// <summary>키를 언어 리소스로 해석해 인자를 채운다. 키/리소스 부재 또는 한국어면 null(호출부가 원문 유지).</summary>
    string? Translate(string? key, string language, IReadOnlyList<string>? args);
}

public sealed class ErrorLocalizer : IErrorLocalizer
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _registry;
    // 언어 → (리소스 키 → 값). 첫 요청 시 로드해 캐시(스레드 안전).
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _cache = new(StringComparer.Ordinal);

    public ErrorLocalizer(IRuleDispatcher dispatcher, IQueryRegistry registry)
    {
        _dispatcher = dispatcher;
        _registry = registry;
    }

    public string? Translate(string? key, string language, IReadOnlyList<string>? args)
    {
        // 한국어는 기본(코드 인라인 폴백)이라 번역하지 않는다 — 원문(Description) 유지.
        if (string.IsNullOrEmpty(key) || string.Equals(language, "KoKr", StringComparison.Ordinal))
            return null;

        var map = _cache.GetOrAdd(language, Load);
        if (!map.TryGetValue(key, out var template) || string.IsNullOrEmpty(template))
            return null;

        var safeArgs = args is { Count: > 0 } ? args.Cast<object>().ToArray() : Array.Empty<object>();
        try { return string.Format(CultureInfo.InvariantCulture, template, safeArgs); }
        catch (FormatException) { return template; }   // 인자 불일치는 템플릿 원문 반환(깨진 메시지 방지)
    }

    private IReadOnlyDictionary<string, string> Load(string language)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!_registry.TryGet("SYS.LanguageResources", out var def) || def is null)
                return map;
            // 동기 경계(결과 필터) — 리소스 로드는 언어당 1회뿐이라 블로킹 비용 무시 가능.
            var rows = _dispatcher.QueryAsync(def.Sql,
                new Dictionary<string, object> { ["language"] = language }, CancellationToken.None)
                .GetAwaiter().GetResult();
            foreach (var row in rows)
            {
                var k = row.TryGetValue("RESOURCE_KEY", out var kv) ? kv?.ToString() : null;
                var v = row.TryGetValue("VALUE", out var vv) ? vv?.ToString() : null;
                if (!string.IsNullOrEmpty(k) && v is not null) map[k] = v;
            }
        }
        catch { /* 리소스 로드 실패는 빈 맵 — 원문(한국어) 폴백으로 동작한다 */ }
        return map;
    }
}
