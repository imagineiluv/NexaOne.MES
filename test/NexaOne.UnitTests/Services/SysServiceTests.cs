using Moq;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Services;

public sealed class SysServiceTests
{
    private static UserService Build(
        Mock<IRoleRepository>? roleMock = null,
        Mock<IMultiLanguageResourceRepository>? langMock = null)
    {
        return new UserService(
            new Mock<IUserRepository>().Object,
            (roleMock ?? new Mock<IRoleRepository>()).Object,
            (langMock ?? new Mock<IMultiLanguageResourceRepository>()).Object,
            new Mock<ILoginFailureHistoryRepository>().Object);
    }

    // ── Role ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRole_new_id_succeeds()
    {
        var roleMock = new Mock<IRoleRepository>();
        roleMock.Setup(r => r.ExistsAsync("ADMIN", default)).ReturnsAsync(false);
        roleMock.Setup(r => r.AddAsync(It.IsAny<Role>(), default)).Returns(Task.CompletedTask);

        var result = await Build(roleMock).CreateRoleAsync("ADMIN", "관리자", "시스템 전체 관리");

        result.IsSuccess.Should().BeTrue();
        result.Value.RoleName.Should().Be("관리자");
        roleMock.Verify(r => r.AddAsync(It.IsAny<Role>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateRole_duplicate_id_returns_conflict()
    {
        var roleMock = new Mock<IRoleRepository>();
        roleMock.Setup(r => r.ExistsAsync("ADMIN", default)).ReturnsAsync(true);

        var result = await Build(roleMock).CreateRoleAsync("ADMIN", "관리자");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("already exists");
    }

    [Fact]
    public async Task GetAllRoles_returns_list()
    {
        var roles = new List<Role> { Role.Create("ADMIN", "관리자"), Role.Create("OPERATOR", "작업자") };
        var roleMock = new Mock<IRoleRepository>();
        roleMock.Setup(r => r.GetAllAsync(default)).ReturnsAsync(roles);

        var result = await Build(roleMock).GetAllRolesAsync();

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetRole_not_found_returns_error()
    {
        var roleMock = new Mock<IRoleRepository>();
        roleMock.Setup(r => r.GetByIdAsync("GHOST", default)).ReturnsAsync((Role?)null);

        var result = await Build(roleMock).GetRoleAsync("GHOST");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AddPermission_role_not_found_fails()
    {
        var roleMock = new Mock<IRoleRepository>();
        roleMock.Setup(r => r.GetByIdAsync("GHOST", default)).ReturnsAsync((Role?)null);

        var result = await Build(roleMock).AddPermissionAsync("GHOST", "MDM.WRITE");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task AddPermission_saves_updated_role()
    {
        var role = Role.Create("ADMIN", "관리자");
        var roleMock = new Mock<IRoleRepository>();
        roleMock.Setup(r => r.GetByIdAsync("ADMIN", default)).ReturnsAsync(role);
        roleMock.Setup(r => r.UpdateAsync(It.IsAny<Role>(), default)).Returns(Task.CompletedTask);

        var result = await Build(roleMock).AddPermissionAsync("ADMIN", "MDM.WRITE");

        result.IsSuccess.Should().BeTrue();
        role.Permissions.Should().Contain("MDM.WRITE");
        roleMock.Verify(r => r.UpdateAsync(It.IsAny<Role>(), default), Times.Once);
    }

    [Fact]
    public async Task RemovePermission_saves_updated_role()
    {
        var role = Role.Create("ADMIN", "관리자");
        role.AddPermission("MDM.WRITE");
        var roleMock = new Mock<IRoleRepository>();
        roleMock.Setup(r => r.GetByIdAsync("ADMIN", default)).ReturnsAsync(role);
        roleMock.Setup(r => r.UpdateAsync(It.IsAny<Role>(), default)).Returns(Task.CompletedTask);

        var result = await Build(roleMock).RemovePermissionAsync("ADMIN", "MDM.WRITE");

        result.IsSuccess.Should().BeTrue();
        role.Permissions.Should().BeEmpty();
        roleMock.Verify(r => r.UpdateAsync(It.IsAny<Role>(), default), Times.Once);
    }

    // ── MultiLanguageResource ─────────────────────────────────────────────────

    [Fact]
    public async Task GetResourcesByMenu_returns_list()
    {
        var resources = new List<MultiLanguageResource>
        {
            MultiLanguageResource.Create("HOME.TITLE", "HOME", LanguageType.KoKr, "대시보드"),
            MultiLanguageResource.Create("HOME.TITLE", "HOME", LanguageType.EnUs, "Dashboard")
        };
        var langMock = new Mock<IMultiLanguageResourceRepository>();
        langMock.Setup(r => r.GetByMenuIdAsync("HOME", default)).ReturnsAsync(resources);

        var result = await Build(langMock: langMock).GetResourcesByMenuAsync("HOME");

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
    }

    [Fact]
    public async Task UpsertResource_new_key_calls_add()
    {
        var langMock = new Mock<IMultiLanguageResourceRepository>();
        langMock.Setup(r => r.ExistsAsync("HOME.TITLE", default)).ReturnsAsync(false);
        langMock.Setup(r => r.AddAsync(It.IsAny<MultiLanguageResource>(), default)).Returns(Task.CompletedTask);

        var result = await Build(langMock: langMock)
            .UpsertResourceAsync("HOME.TITLE", "HOME", LanguageType.KoKr, "대시보드");

        result.IsSuccess.Should().BeTrue();
        langMock.Verify(r => r.AddAsync(It.IsAny<MultiLanguageResource>(), default), Times.Once);
        langMock.Verify(r => r.UpdateAsync(It.IsAny<MultiLanguageResource>(), default), Times.Never);
    }

    [Fact]
    public async Task UpsertResource_existing_key_calls_update()
    {
        var langMock = new Mock<IMultiLanguageResourceRepository>();
        langMock.Setup(r => r.ExistsAsync("HOME.TITLE", default)).ReturnsAsync(true);
        langMock.Setup(r => r.UpdateAsync(It.IsAny<MultiLanguageResource>(), default)).Returns(Task.CompletedTask);

        var result = await Build(langMock: langMock)
            .UpsertResourceAsync("HOME.TITLE", "HOME", LanguageType.KoKr, "홈 화면");

        result.IsSuccess.Should().BeTrue();
        langMock.Verify(r => r.UpdateAsync(It.IsAny<MultiLanguageResource>(), default), Times.Once);
        langMock.Verify(r => r.AddAsync(It.IsAny<MultiLanguageResource>(), default), Times.Never);
    }
}
