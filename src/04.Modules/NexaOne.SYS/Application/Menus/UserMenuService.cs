using NexaOne.Common;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Menus;

/// <summary>
/// 즐겨찾기 / 최근 메뉴 (설계서 20.12).
/// - 즐겨찾기: 중복 추가는 멱등 처리(현행 '중복 시 기존 활성화' 규칙의 적응), 순서는 DISPLAY_SEQUENCE.
/// - 최근 메뉴: 동일 메뉴 재사용 시 맨 앞으로, 최대 <see cref="MaxRecentMenus"/>개 유지 (현행 RecentMenuCount=10).
/// - 권한이 제거된 메뉴는 행을 지우지 않고 조회 시 권한 메뉴와 교차해 숨긴다 — 권한 복원 시 다시 보인다.
/// - 대상은 Screen 메뉴만 — 폴더는 즐겨찾기/최근 기록 의미가 없다.
/// </summary>
public sealed class UserMenuService
{
    /// <summary>최근 메뉴 보관 개수 — 현행 App.config RecentMenuCount=10 대응.</summary>
    public const int MaxRecentMenus = 10;

    /// <summary>즐겨찾기 상한 — 무제한 누적 방지용 웹 적응 한도 (현행에는 명시 한도 없음).</summary>
    public const int MaxFavoriteMenus = 50;

    private readonly IFavoriteMenuRepository _favorites;
    private readonly IRecentMenuRepository _recents;
    private readonly IMenuRepository _menus;

    public UserMenuService(
        IFavoriteMenuRepository favorites,
        IRecentMenuRepository recents,
        IMenuRepository menus)
    {
        _favorites = favorites;
        _recents = recents;
        _menus = menus;
    }

    // ── 즐겨찾기 ──────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<FavoriteMenuEntry>>> GetFavoritesAsync(
        string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure<IReadOnlyList<FavoriteMenuEntry>>(Error.Validation("UserId is required."));

        var authorized = await GetAuthorizedScreensAsync(userId, ct);
        var rows = await _favorites.GetByUserAsync(userId, ct);
        IReadOnlyList<FavoriteMenuEntry> entries = rows
            .Select(f => (Fav: f, Menu: authorized.GetValueOrDefault(f.MenuId)))
            .Where(x => x.Menu is not null)   // 권한 제거/메뉴 삭제 → 행 유지 + 렌더링 제외 (설계 20.12)
            .OrderBy(x => x.Fav.DisplaySequence)
            .ThenBy(x => x.Fav.MenuId, StringComparer.OrdinalIgnoreCase)
            .Select(x => new FavoriteMenuEntry(x.Menu!, x.Fav.DisplaySequence))
            .ToList();
        return Result.Success(entries);
    }

    public async Task<Result> AddFavoriteAsync(string userId, string menuId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure(Error.Validation("MenuId is required."));

        menuId = menuId.Trim();
        // 권한 있는 Screen 메뉴만 등록 허용 — 임의 문자열 누적을 입력 단계에서 차단한다
        var authorized = await GetAuthorizedScreensAsync(userId, ct);
        if (!authorized.TryGetValue(menuId, out var menu))
            return Result.Failure(Error.NotFound("권한이 있는 메뉴가 아닙니다."));

        var existing = await _favorites.GetByUserAsync(userId, ct);
        // 현행 SaveFavoriteMenu Rule '중복 시 기존 활성화' — 소프트 삭제가 없는 웹 적응에선 멱등 성공
        if (existing.Any(f => string.Equals(f.MenuId, menu.MenuId, StringComparison.OrdinalIgnoreCase)))
            return Result.Success();
        // 한도/순번은 check-then-act라 동시 추가 race에서 한도 1~2개 초과나 순번 중복이 가능하다 —
        // 본인 계정의 개인화 데이터이고 표시 정렬(DisplaySequence, MenuId)이 결정적이라 잠금 없이 수용한다.
        if (existing.Count >= MaxFavoriteMenus)
            return Result.Failure(Error.Validation($"즐겨찾기는 최대 {MaxFavoriteMenus}개까지 등록할 수 있습니다."));

        var next = existing.Count == 0 ? 1 : existing.Max(f => f.DisplaySequence) + 1;
        await _favorites.UpsertAsync(FavoriteMenu.Create(userId, menu.MenuId, next), ct);
        return Result.Success();
    }

    public async Task<Result> RemoveFavoriteAsync(string userId, string menuId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure(Error.Validation("MenuId is required."));

        // 권한 숨김 상태의 즐겨찾기도 정리할 수 있어야 하므로 권한 검사 없이 삭제한다 (멱등)
        await _favorites.DeleteAsync(userId, menuId.Trim(), ct);
        return Result.Success();
    }

    /// <summary>
    /// 전달된 순서대로 DISPLAY_SEQUENCE를 재부여한다 — 현행 드래그 정렬의 웹 적응(up/down).
    /// 목록에 없는 즐겨찾기(권한 숨김 등)는 기존 상대 순서를 유지한 채 뒤로 보낸다.
    /// </summary>
    public async Task<Result> ReorderFavoritesAsync(
        string userId, IReadOnlyList<string> orderedMenuIds, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure(Error.Validation("UserId is required."));
        if (orderedMenuIds is null || orderedMenuIds.Count == 0)
            return Result.Failure(Error.Validation("정렬할 메뉴 목록이 비어 있습니다."));

        var existing = await _favorites.GetByUserAsync(userId, ct);
        var byId = new Dictionary<string, FavoriteMenu>(StringComparer.OrdinalIgnoreCase);
        foreach (var fav in existing)
            byId[fav.MenuId] = fav;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<FavoriteMenu>();
        foreach (var raw in orderedMenuIds)
        {
            var id = (raw ?? string.Empty).Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;
            // 등록되지 않은 menuId는 무시 — 다른 탭에서 막 삭제된 항목이 섞인 재정렬 요청도 수용한다
            if (byId.TryGetValue(id, out var fav)) ordered.Add(fav);
        }

        var rest = existing
            .Where(f => !seen.Contains(f.MenuId))
            .OrderBy(f => f.DisplaySequence)
            .ToList();

        var sequence = 0;
        foreach (var fav in ordered.Concat(rest))
        {
            sequence++;
            if (fav.DisplaySequence == sequence) continue;   // 변경분만 저장
            fav.ChangeSequence(sequence);
            await _favorites.UpsertAsync(fav, ct);
        }
        return Result.Success();
    }

    // ── 최근 메뉴 ─────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<RecentMenuEntry>>> GetRecentAsync(
        string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure<IReadOnlyList<RecentMenuEntry>>(Error.Validation("UserId is required."));

        var authorized = await GetAuthorizedScreensAsync(userId, ct);
        var rows = await _recents.GetByUserAsync(userId, ct);
        IReadOnlyList<RecentMenuEntry> entries = rows
            .Select(r => (Recent: r, Menu: authorized.GetValueOrDefault(r.MenuId)))
            .Where(x => x.Menu is not null)   // 권한 제거 → 숨김 (행 유지, 설계 20.12)
            .OrderByDescending(x => x.Recent.LastUsedAt)
            .Take(MaxRecentMenus)
            .Select(x => new RecentMenuEntry(x.Menu!, x.Recent.LastUsedAt))
            .ToList();
        return Result.Success(entries);
    }

    /// <summary>화면 사용 기록 — 동일 메뉴는 LastUsedAt 갱신(맨 앞 이동), 한도 초과분은 오래된 것부터 삭제.</summary>
    public async Task<Result> RecordRecentAsync(string userId, string menuId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure(Error.Validation("UserId is required."));
        if (string.IsNullOrWhiteSpace(menuId))
            return Result.Failure(Error.Validation("MenuId is required."));

        // 탐색 부수 기록이므로 권한 밖/미등록 화면은 오류 대신 조용히 무시한다
        var authorized = await GetAuthorizedScreensAsync(userId, ct);
        if (!authorized.TryGetValue(menuId.Trim(), out var menu))
            return Result.Success();

        // 정식 MenuId로 정규화 저장 — 대소문자 변형이 별개 행이 되는 것을 막는다
        await _recents.UpsertAsync(RecentMenu.Create(userId, menu.MenuId), ct);

        var all = await _recents.GetByUserAsync(userId, ct);
        foreach (var stale in all.OrderByDescending(r => r.LastUsedAt).Skip(MaxRecentMenus))
            await _recents.DeleteAsync(userId, stale.MenuId, ct);
        return Result.Success();
    }

    // 권한 있는 Screen 메뉴 맵 — 키는 MenuId, DB 콜레이션(CI)과 일관되게 OrdinalIgnoreCase 비교
    private async Task<Dictionary<string, MenuItem>> GetAuthorizedScreensAsync(
        string userId, CancellationToken ct)
    {
        var menus = await _menus.GetAuthorizedMenusAsync(userId, ct);
        var map = new Dictionary<string, MenuItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in menus.Where(m => m.MenuType == MenuItemType.Screen))
            map[m.MenuId] = m;
        return map;
    }
}

/// <summary>즐겨찾기 조회 결과 — 권한 메뉴와 교차된 항목만 담긴다.</summary>
public sealed record FavoriteMenuEntry(MenuItem Menu, int DisplaySequence);

/// <summary>최근 메뉴 조회 결과 — 권한 메뉴와 교차된 항목만 담긴다.</summary>
public sealed record RecentMenuEntry(MenuItem Menu, DateTime LastUsedAt);
