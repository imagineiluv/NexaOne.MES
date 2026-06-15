using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.API.Controllers;
using NexaOne.Common;
using NexaOne.SYS.Application.Conditions;
using NexaOne.SYS.Application.Menus;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Controllers;

/// <summary>§20.10 — SysController 사용자 엔드포인트: 관리자 잠금 해제,
/// 도메인 User 대신 DTO 반환(PASSWORD_HASH 비노출)을 검증한다.</summary>
public sealed class SysControllerUserTests
{
    private static SysController BuildController(Mock<IUserRepository> repo)
    {
        var userService = new UserService(
            repo.Object,
            new Mock<IRoleRepository>().Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            new Mock<ILoginFailureHistoryRepository>().Object);
        var menuService = new MenuService(
            new Mock<IMenuRepository>().Object,
            NullLogger<MenuService>.Instance);

        return new SysController(
            userService, menuService,
            new ConditionSettingService(new Mock<IConditionSettingRepository>().Object),
            new UserMenuService(
                new Mock<IFavoriteMenuRepository>().Object,
                new Mock<IRecentMenuRepository>().Object,
                new Mock<IMenuRepository>().Object))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "admin1") }, "test"))
                }
            }
        };
    }

    private static User LockedUser() => User.Restore(
        "u001", "Alice", PasswordHasher.Hash("pw123!"), "alice@test.com", "OPERATOR", LanguageType.KoKr,
        isActive: true, isDeleted: false, deletedAt: null, lastLoginAt: null,
        passwordState: PasswordState.Normal,
        failCount: User.MaxConsecutiveFailures,
        lockedUntil: DateTime.UtcNow.AddMinutes(10));

    // ── UnlockUser ────────────────────────────────────────────────────────────

    [Fact]
    public async Task UnlockUser_clears_lock_and_returns_204()
    {
        var user = LockedUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await BuildController(repo).UnlockUser("u001", default);

        result.Should().BeOfType<NoContentResult>();
        user.LockedUntil.Should().BeNull("잠금 해제 후에는 즉시 로그인 가능해야 한다");
        user.FailCount.Should().Be(0, "해제 시 실패 카운터도 초기화돼야 한다");
        repo.Verify(r => r.UpdateAsync(user, default), Times.Once);
    }

    [Fact]
    public async Task UnlockUser_unknown_user_returns_404()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);

        var result = await BuildController(repo).UnlockUser("ghost", default);

        result.Should().BeOfType<NotFoundObjectResult>();
        repo.Verify(r => r.UpdateAsync(It.IsAny<User>(), default), Times.Never);
    }

    // ── DTO 매핑 (해시 비노출) ────────────────────────────────────────────────

    [Fact]
    public async Task GetUser_returns_dto_without_password_hash()
    {
        var user = LockedUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);

        var result = await BuildController(repo).GetUser("u001", default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<UserDto>("도메인 User를 직렬화하면 PASSWORD_HASH가 노출된다").Subject;
        dto.Id.Should().Be("u001");
        dto.Language.Should().Be("KoKr", "enum은 숫자가 아닌 이름으로 직렬화돼야 한다");
        dto.PasswordState.Should().Be("Normal");
        dto.LockedUntil.Should().NotBeNull();
        typeof(UserDto).GetProperty("PasswordHash").Should().BeNull(
            "응답 DTO에 비밀번호 해시 속성이 존재하면 안 된다");
    }

    [Fact]
    public async Task GetUsers_returns_dto_list_without_password_hash()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetAllActiveAsync(default)).ReturnsAsync(new List<User> { LockedUser() });

        var result = await BuildController(repo).GetUsers(default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var items = ok.Value.Should().BeAssignableTo<IEnumerable<UserDto>>().Subject.ToList();
        items.Should().ContainSingle().Which.Id.Should().Be("u001");
    }
}
