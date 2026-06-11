using NexaOne.Web.Services.Api;

namespace NexaOne.Web.Services;

/// <summary>
/// §20.12 — 즐겨찾기/최근 메뉴 개인화 상태. NavMenu(렌더/토글)와 MdiTabBar(최근 기록)가 공유한다.
/// 권한 교차·한도·정렬은 서버(UserMenuService)가 책임지므로 여기서는 캐시와 변경 통지만 담당한다.
/// </summary>
public sealed class MenuPersonalizationService(IApiClient api)
{
    public IReadOnlyList<FavoriteMenuDto> Favorites { get; private set; } = [];
    public IReadOnlyList<RecentMenuDto> Recent { get; private set; } = [];

    /// <summary>즐겨찾기/최근 목록 변경 통지 — NavMenu 재렌더용.</summary>
    public event Action? Changed;

    // DB CI 콜레이션과 일관되게 대소문자 무시 비교 (§20.12 백엔드와 동일 규약)
    public bool IsFavorite(string menuId) =>
        Favorites.Any(f => string.Equals(f.MenuId, menuId, StringComparison.OrdinalIgnoreCase));

    public async Task LoadAsync(CancellationToken ct = default)
    {
        var favorites = api.GetFavoriteMenusAsync(ct);
        var recent = api.GetRecentMenusAsync(ct);
        Favorites = await favorites;
        Recent = await recent;
        Changed?.Invoke();
    }

    /// <summary>☆/★ 토글. 서버 반영 성공 시에만 목록을 다시 읽는다 — 낙관적 갱신 금지(§20.8과 동일 원칙).</summary>
    public async Task<bool> ToggleFavoriteAsync(string menuId, CancellationToken ct = default)
    {
        var ok = IsFavorite(menuId)
            ? await api.RemoveFavoriteMenuAsync(menuId, ct)
            : await api.AddFavoriteMenuAsync(menuId, ct);
        if (ok) await LoadAsync(ct);
        return ok;
    }

    /// <summary>현행 드래그 정렬의 웹 적응 — ▲/▼로 한 칸 이동 후 전체 순서를 서버에 반영한다 (설계 20.12).</summary>
    public async Task<bool> MoveFavoriteAsync(string menuId, int delta, CancellationToken ct = default)
    {
        var ids = Favorites.Select(f => f.MenuId).ToList();
        var idx = ids.FindIndex(id => string.Equals(id, menuId, StringComparison.OrdinalIgnoreCase));
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= ids.Count) return false;

        (ids[idx], ids[target]) = (ids[target], ids[idx]);
        var ok = await api.ReorderFavoriteMenusAsync(ids, ct);
        if (ok) await LoadAsync(ct);
        return ok;
    }

    /// <summary>화면 진입 기록 — 탐색의 부수 효과이므로 실패는 조용히 무시한다 (설계 20.12).</summary>
    public async Task RecordRecentAsync(string menuId)
    {
        try
        {
            if (await api.RecordRecentMenuAsync(menuId))
            {
                Recent = await api.GetRecentMenusAsync();
                Changed?.Invoke();
            }
        }
        catch { /* 부수 기록 실패는 화면 전환에 영향을 주지 않는다 */ }
    }
}
