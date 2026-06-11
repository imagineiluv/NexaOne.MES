using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.API.Controllers;
using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Controllers;

/// <summary>§20.12 — SysController 즐겨찾기/최근 메뉴 엔드포인트: DTO 매핑과
/// 권한 교차/조용한 무시의 HTTP 응답 변환을 검증한다.</summary>
public sealed class SysControllerMenuPersonalizationTests
{
    private static MenuItem Screen(string menuId, string programId) =>
        MenuItem.Create(menuId, $"{menuId} 화면", null, 1, MenuItemType.Screen, programId, "UI-" + menuId, "icon-x");

    private static SysController BuildController(
        Mock<IFavoriteMenuRepository> favRepo,
        Mock<IRecentMenuRepository> recentRepo,
        Mock<IMenuRepository> menuRepo)
    {
        var userService = new UserService(
            new Mock<IUserRepository>().Object,
            new Mock<IRoleRepository>().Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            new Mock<ILoginFailureHistoryRepository>().Object);
        var menuService = new MenuService(menuRepo.Object, NullLogger<MenuService>.Instance);

        return new SysController(
            userService, menuService,
            new ConditionSettingService(new Mock<IConditionSettingRepository>().Object),
            new UserMenuService(favRepo.Object, recentRepo.Object, menuRepo.Object))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "user1") }, "test"))
                }
            }
        };
    }

    private static (Mock<IFavoriteMenuRepository> Fav, Mock<IRecentMenuRepository> Recent, Mock<IMenuRepository> Menu)
        Repos(params MenuItem[] authorized)
    {
        var fav = new Mock<IFavoriteMenuRepository>();
        var recent = new Mock<IRecentMenuRepository>();
        var menu = new Mock<IMenuRepository>();
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<FavoriteMenu>());
        recent.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<RecentMenu>());
        menu.Setup(r => r.GetAuthorizedMenusAsync("user1", default)).ReturnsAsync(authorized.ToList());
        return (fav, recent, menu);
    }

    // ── GetFavorites / AddFavorite / RemoveFavorite ───────────────────────────

    [Fact]
    public async Task GetFavorites_returns_dto_with_menu_fields()
    {
        var (fav, recent, menu) = Repos(Screen("MENU-A", "/fdc/monitor"));
        fav.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<FavoriteMenu>
        {
            FavoriteMenu.Restore("user1", "MENU-A", 3, DateTime.UtcNow),
        });

        var result = await BuildController(fav, recent, menu).GetFavorites(default);

        var items = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<FavoriteMenuDto>>().Subject.ToList();
        var dto = items.Should().ContainSingle().Subject;
        dto.MenuId.Should().Be("MENU-A");
        dto.MenuName.Should().Be("MENU-A 화면");
        dto.ProgramId.Should().Be("/fdc/monitor", "NavLink href로 그대로 쓰인다");
        dto.ImageId.Should().Be("icon-x");
        dto.DisplaySequence.Should().Be(3);
    }

    [Fact]
    public async Task AddFavorite_authorized_returns_204()
    {
        var (fav, recent, menu) = Repos(Screen("MENU-A", "/fdc/monitor"));

        var result = await BuildController(fav, recent, menu)
            .AddFavorite(new FavoriteMenuRequest("MENU-A"), default);

        result.Should().BeOfType<NoContentResult>();
        fav.Verify(r => r.UpsertAsync(It.Is<FavoriteMenu>(f => f.MenuId == "MENU-A"), default), Times.Once);
    }

    [Fact]
    public async Task AddFavorite_unauthorized_returns_400()
    {
        var (fav, recent, menu) = Repos();   // 권한 메뉴 없음

        var result = await BuildController(fav, recent, menu)
            .AddFavorite(new FavoriteMenuRequest("MENU-X"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        fav.Verify(r => r.UpsertAsync(It.IsAny<FavoriteMenu>(), default), Times.Never);
    }

    [Fact]
    public async Task RemoveFavorite_returns_204_and_deletes()
    {
        var (fav, recent, menu) = Repos();

        var result = await BuildController(fav, recent, menu).RemoveFavorite("MENU-A", default);

        result.Should().BeOfType<NoContentResult>();
        fav.Verify(r => r.DeleteAsync("user1", "MENU-A", default), Times.Once);
    }

    [Fact]
    public async Task ReorderFavorites_null_list_returns_400()
    {
        var (fav, recent, menu) = Repos();

        var result = await BuildController(fav, recent, menu)
            .ReorderFavorites(new ReorderFavoritesRequest(null), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    // ── GetRecentMenus / RecordRecentMenu ─────────────────────────────────────

    [Fact]
    public async Task GetRecentMenus_returns_dto_with_last_used_at()
    {
        var usedAt = DateTime.UtcNow.AddMinutes(-5);
        var (fav, recent, menu) = Repos(Screen("MENU-A", "/fdc/monitor"));
        recent.Setup(r => r.GetByUserAsync("user1", default)).ReturnsAsync(new List<RecentMenu>
        {
            RecentMenu.Restore("user1", "MENU-A", usedAt),
        });

        var result = await BuildController(fav, recent, menu).GetRecentMenus(default);

        var items = result.Should().BeOfType<OkObjectResult>().Subject
            .Value.Should().BeAssignableTo<IEnumerable<RecentMenuDto>>().Subject.ToList();
        var dto = items.Should().ContainSingle().Subject;
        dto.MenuId.Should().Be("MENU-A");
        dto.LastUsedAt.Should().Be(usedAt);
    }

    [Fact]
    public async Task RecordRecentMenu_authorized_returns_204_and_upserts()
    {
        var (fav, recent, menu) = Repos(Screen("MENU-A", "/fdc/monitor"));

        var result = await BuildController(fav, recent, menu)
            .RecordRecentMenu(new RecentMenuRequest("MENU-A"), default);

        result.Should().BeOfType<NoContentResult>();
        recent.Verify(r => r.UpsertAsync(It.Is<RecentMenu>(m => m.MenuId == "MENU-A"), default), Times.Once);
    }

    [Fact]
    public async Task RecordRecentMenu_unauthorized_returns_204_without_write()
    {
        // 탐색 부수 기록 — 권한 밖 화면도 오류가 아니라 조용히 무시 (설계 20.12)
        var (fav, recent, menu) = Repos();

        var result = await BuildController(fav, recent, menu)
            .RecordRecentMenu(new RecentMenuRequest("MENU-X"), default);

        result.Should().BeOfType<NoContentResult>();
        recent.Verify(r => r.UpsertAsync(It.IsAny<RecentMenu>(), default), Times.Never);
    }
}
