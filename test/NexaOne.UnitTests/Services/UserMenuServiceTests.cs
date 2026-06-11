using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Services;

/// <summary>§20.12 — 즐겨찾기/최근 메뉴: 권한 교차 숨김, 멱등 추가, 한도, 재정렬,
/// 최근 기록 trim과 조용한 무시를 검증한다.</summary>
public sealed class UserMenuServiceTests
{
    private static MenuItem Screen(string menuId, string programId = "/fdc/monitor") =>
        MenuItem.Create(menuId, $"{menuId} 화면", null, 1, MenuItemType.Screen, programId, "UI-" + menuId);

    private static MenuItem Folder(string menuId) =>
        MenuItem.Create(menuId, $"{menuId} 폴더", null, 1, MenuItemType.Folder, "", "");

    private static (UserMenuService Service, Mock<IFavoriteMenuRepository> Fav,
        Mock<IRecentMenuRepository> Recent, Mock<IMenuRepository> Menu)
        Build(params MenuItem[] authorizedMenus)
    {
        var fav = new Mock<IFavoriteMenuRepository>();
        var recent = new Mock<IRecentMenuRepository>();
        var menu = new Mock<IMenuRepository>();
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<FavoriteMenu>());
        recent.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<RecentMenu>());
        menu.Setup(r => r.GetAuthorizedMenusAsync("user1", default)).ReturnsAsync(authorizedMenus.ToList());
        return (new UserMenuService(fav.Object, recent.Object, menu.Object), fav, recent, menu);
    }

    // ── GetFavoritesAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetFavorites_hides_unauthorized_rows_and_orders_by_sequence()
    {
        var (service, fav, _, _) = Build(Screen("MENU-A"), Screen("MENU-B"));
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<FavoriteMenu>
        {
            FavoriteMenu.Restore("user1", "MENU-B", 2, DateTime.UtcNow),
            FavoriteMenu.Restore("user1", "MENU-A", 1, DateTime.UtcNow),
            FavoriteMenu.Restore("user1", "MENU-X", 0, DateTime.UtcNow),   // 권한 제거됨 — 행 유지, 표시만 제외
        });

        var result = await service.GetFavoritesAsync("user1");

        result.IsSuccess.Should().BeTrue();
        result.Value.Select(e => e.Menu.MenuId).Should().Equal("MENU-A", "MENU-B");
    }

    [Fact]
    public async Task GetFavorites_matches_menu_id_case_insensitively()
    {
        // DB CI 콜레이션과 일관 — 소문자 변형으로 저장된 기존 행도 표시돼야 한다
        var (service, fav, _, _) = Build(Screen("MENU-A"));
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<FavoriteMenu>
        {
            FavoriteMenu.Restore("user1", "menu-a", 1, DateTime.UtcNow),
        });

        var result = await service.GetFavoritesAsync("user1");

        result.Value.Should().ContainSingle().Which.Menu.MenuId.Should().Be("MENU-A");
    }

    // ── AddFavoriteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task AddFavorite_unauthorized_menu_returns_not_found()
    {
        var (service, fav, _, _) = Build(Screen("MENU-A"));

        var result = await service.AddFavoriteAsync("user1", "MENU-FORBIDDEN");

        result.IsFailure.Should().BeTrue("임의 문자열 누적은 입력 단계에서 차단돼야 한다");
        fav.Verify(r => r.UpsertAsync(It.IsAny<FavoriteMenu>(), default), Times.Never);
    }

    [Fact]
    public async Task AddFavorite_folder_menu_returns_not_found()
    {
        var (service, fav, _, _) = Build(Folder("FOLDER-A"));

        var result = await service.AddFavoriteAsync("user1", "FOLDER-A");

        result.IsFailure.Should().BeTrue("폴더는 즐겨찾기 대상이 아니다");
        fav.Verify(r => r.UpsertAsync(It.IsAny<FavoriteMenu>(), default), Times.Never);
    }

    [Fact]
    public async Task AddFavorite_duplicate_is_idempotent_success()
    {
        // 현행 SaveFavoriteMenu '중복 시 기존 활성화' 규칙의 웹 적응 — 멱등 성공
        var (service, fav, _, _) = Build(Screen("MENU-A"));
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<FavoriteMenu>
        {
            FavoriteMenu.Restore("user1", "menu-a", 1, DateTime.UtcNow),   // 대소문자 변형도 중복으로 인식
        });

        var result = await service.AddFavoriteAsync("user1", "MENU-A");

        result.IsSuccess.Should().BeTrue();
        fav.Verify(r => r.UpsertAsync(It.IsAny<FavoriteMenu>(), default), Times.Never);
    }

    [Fact]
    public async Task AddFavorite_over_limit_fails()
    {
        var menus = Enumerable.Range(0, UserMenuService.MaxFavoriteMenus + 1)
            .Select(i => Screen($"MENU-{i}")).ToArray();
        var (service, fav, _, _) = Build(menus);
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(
            Enumerable.Range(0, UserMenuService.MaxFavoriteMenus)
                .Select(i => FavoriteMenu.Restore("user1", $"MENU-{i}", i + 1, DateTime.UtcNow))
                .ToList());

        var result = await service.AddFavoriteAsync("user1", $"MENU-{UserMenuService.MaxFavoriteMenus}");

        result.IsFailure.Should().BeTrue();
        fav.Verify(r => r.UpsertAsync(It.IsAny<FavoriteMenu>(), default), Times.Never);
    }

    [Fact]
    public async Task AddFavorite_stores_canonical_menu_id_with_next_sequence()
    {
        var (service, fav, _, _) = Build(Screen("MENU-A"), Screen("MENU-B"));
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<FavoriteMenu>
        {
            FavoriteMenu.Restore("user1", "MENU-A", 7, DateTime.UtcNow),
        });

        var result = await service.AddFavoriteAsync("user1", " menu-b ");

        result.IsSuccess.Should().BeTrue();
        fav.Verify(r => r.UpsertAsync(It.Is<FavoriteMenu>(f =>
            f.MenuId == "MENU-B" &&          // 정식 표기로 정규화 저장
            f.DisplaySequence == 8), default), Times.Once);
    }

    [Fact]
    public async Task RemoveFavorite_deletes_without_permission_check()
    {
        // 권한이 제거돼 숨겨진 즐겨찾기도 정리할 수 있어야 한다
        var (service, fav, _, _) = Build();   // 권한 메뉴 없음

        var result = await service.RemoveFavoriteAsync("user1", "MENU-HIDDEN");

        result.IsSuccess.Should().BeTrue();
        fav.Verify(r => r.DeleteAsync("user1", "MENU-HIDDEN", default), Times.Once);
    }

    // ── ReorderFavoritesAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ReorderFavorites_empty_list_fails()
    {
        var (service, _, _, _) = Build();

        var result = await service.ReorderFavoritesAsync("user1", []);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ReorderFavorites_applies_order_and_moves_unlisted_to_back()
    {
        var (service, fav, _, _) = Build();
        var a = FavoriteMenu.Restore("user1", "A", 1, DateTime.UtcNow);
        var b = FavoriteMenu.Restore("user1", "B", 2, DateTime.UtcNow);
        var c = FavoriteMenu.Restore("user1", "C", 3, DateTime.UtcNow);
        fav.Setup(r => r.GetByUserAsync("user1", default))
            .ReturnsAsync(new List<FavoriteMenu> { a, b, c });

        // C를 맨 앞으로 — B는 요청에 없음(권한 숨김 가정) → 기존 상대 순서 유지하며 뒤로
        var result = await service.ReorderFavoritesAsync("user1", ["C", "A"]);

        result.IsSuccess.Should().BeTrue();
        c.DisplaySequence.Should().Be(1);
        a.DisplaySequence.Should().Be(2);
        b.DisplaySequence.Should().Be(3);
        // 변경분만 저장 — A는 1→2, B는 2→3, C는 3→1 모두 변경
        fav.Verify(r => r.UpsertAsync(It.IsAny<FavoriteMenu>(), default), Times.Exactly(3));
    }

    [Fact]
    public async Task ReorderFavorites_skips_unchanged_rows()
    {
        var (service, fav, _, _) = Build();
        var a = FavoriteMenu.Restore("user1", "A", 1, DateTime.UtcNow);
        var b = FavoriteMenu.Restore("user1", "B", 2, DateTime.UtcNow);
        fav.Setup(r => r.GetByUserAsync("user1", default))
            .ReturnsAsync(new List<FavoriteMenu> { a, b });

        var result = await service.ReorderFavoritesAsync("user1", ["A", "B"]);

        result.IsSuccess.Should().BeTrue();
        fav.Verify(r => r.UpsertAsync(It.IsAny<FavoriteMenu>(), default), Times.Never,
            "순서가 그대로면 불필요한 UPDATE를 내보내지 않아야 한다");
    }

    [Fact]
    public async Task ReorderFavorites_ignores_unknown_and_duplicate_ids()
    {
        var (service, fav, _, _) = Build();
        var a = FavoriteMenu.Restore("user1", "A", 1, DateTime.UtcNow);
        var b = FavoriteMenu.Restore("user1", "B", 2, DateTime.UtcNow);
        fav.Setup(r => r.GetByUserAsync("user1", default))
            .ReturnsAsync(new List<FavoriteMenu> { a, b });

        // 다른 탭에서 막 삭제된 "GHOST", 중복 "b"(대소문자 변형)가 섞여도 수용
        var result = await service.ReorderFavoritesAsync("user1", ["B", "GHOST", "b", "A"]);

        result.IsSuccess.Should().BeTrue();
        b.DisplaySequence.Should().Be(1);
        a.DisplaySequence.Should().Be(2);
    }

    // ── GetRecentAsync / RecordRecentAsync ────────────────────────────────────

    [Fact]
    public async Task GetRecent_orders_desc_and_takes_at_most_max()
    {
        var now = DateTime.UtcNow;
        var menus = Enumerable.Range(0, 12).Select(i => Screen($"MENU-{i}")).ToArray();
        var (service, _, recent, _) = Build(menus);
        recent.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(
            Enumerable.Range(0, 12)
                .Select(i => RecentMenu.Restore("user1", $"MENU-{i}", now.AddMinutes(-i)))
                .ToList());

        var result = await service.GetRecentAsync("user1");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(UserMenuService.MaxRecentMenus);
        result.Value[0].Menu.MenuId.Should().Be("MENU-0", "가장 최근 사용한 메뉴가 맨 앞이어야 한다");
    }

    [Fact]
    public async Task GetRecent_hides_unauthorized_rows()
    {
        var now = DateTime.UtcNow;
        var (service, _, recent, _) = Build(Screen("MENU-A"));
        recent.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<RecentMenu>
        {
            RecentMenu.Restore("user1", "MENU-A", now),
            RecentMenu.Restore("user1", "MENU-X", now.AddMinutes(1)),   // 권한 제거 — 더 최근이어도 숨김
        });

        var result = await service.GetRecentAsync("user1");

        result.Value.Should().ContainSingle().Which.Menu.MenuId.Should().Be("MENU-A");
    }

    [Fact]
    public async Task RecordRecent_unauthorized_menu_is_silently_ignored()
    {
        var (service, _, recent, _) = Build(Screen("MENU-A"));

        var result = await service.RecordRecentAsync("user1", "MENU-FORBIDDEN");

        result.IsSuccess.Should().BeTrue("탐색 부수 기록이므로 권한 밖 화면은 오류가 아니라 무시여야 한다");
        recent.Verify(r => r.UpsertAsync(It.IsAny<RecentMenu>(), default), Times.Never);
    }

    [Fact]
    public async Task RecordRecent_upserts_canonical_id_and_trims_oldest()
    {
        var now = DateTime.UtcNow;
        var menus = Enumerable.Range(0, 12).Select(i => Screen($"MENU-{i}")).ToArray();
        var (service, _, recent, _) = Build(menus);
        // 업서트 후 재조회 시점에 11건 — 한도(10) 초과분 1건은 가장 오래된 것부터 삭제
        recent.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(
            Enumerable.Range(0, 11)
                .Select(i => RecentMenu.Restore("user1", $"MENU-{i}", now.AddMinutes(-i)))
                .ToList());

        var result = await service.RecordRecentAsync("user1", " menu-0 ");

        result.IsSuccess.Should().BeTrue();
        recent.Verify(r => r.UpsertAsync(It.Is<RecentMenu>(m => m.MenuId == "MENU-0"), default), Times.Once,
            "대소문자 변형이 별개 행이 되지 않도록 정식 MenuId로 저장해야 한다");
        recent.Verify(r => r.DeleteAsync("user1", "MENU-10", default), Times.Once);
        recent.Verify(r => r.DeleteAsync("user1", It.IsAny<string>(), default), Times.Once,
            "한도 초과분만 삭제해야 한다");
    }
}
