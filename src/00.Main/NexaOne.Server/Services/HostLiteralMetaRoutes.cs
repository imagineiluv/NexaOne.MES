using Microsoft.AspNetCore.Components;

namespace NexaOne.Server.Services;

/// <summary>호스트 전용(리터럴 라우트) 메타 화면 UI_ID 레지스트리 — @page "/meta/{리터럴}"로 점등된
/// 화면(HostUserRequests·HostMrpPlanning 등)은 IScreenDefinitionProvider에 정의가 없어도 동작한다.
/// 사이드바 '준비 중' 판정이 정의 등록만 보면 이 화면들을 오표시하므로(실사고: 4화면 회색 처리),
/// 호스트 어셈블리의 RouteAttribute를 1회 스캔해 파라미터 없는 /meta/ 리터럴 경로를 점등으로 집계한다.
/// 새 리터럴 페이지는 @page만 달면 자동 반영 — 별도 등록 불필요.</summary>
public static class HostLiteralMetaRoutes
{
    private static readonly Lazy<HashSet<string>> _uiIds = new(Scan);

    public static bool Contains(string? uiId)
        => !string.IsNullOrEmpty(uiId) && _uiIds.Value.Contains(uiId);

    private static HashSet<string> Scan()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in typeof(HostLiteralMetaRoutes).Assembly.GetTypes())
        {
            if (!typeof(IComponent).IsAssignableFrom(type)) continue;
            foreach (var route in type.GetCustomAttributes(typeof(RouteAttribute), inherit: false).Cast<RouteAttribute>())
            {
                var t = route.Template;
                // 파라미터 라우트(/meta/{UiId})는 메타 폴백이므로 제외 — 리터럴만 점등으로 본다.
                if (t.StartsWith("/meta/", StringComparison.OrdinalIgnoreCase) && !t.Contains('{'))
                    set.Add(t["/meta/".Length..]);
            }
        }
        return set;
    }
}
