using NexaOne.Common;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class SysDomainTests
{
    // ── Role ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_role_valid_succeeds()
    {
        var role = Role.Create("ADMIN", "관리자", "시스템 전체 관리");
        role.RoleName.Should().Be("관리자");
        role.Description.Should().Be("시스템 전체 관리");
        role.Permissions.Should().BeEmpty();
    }

    [Fact]
    public void Create_role_without_description_succeeds()
    {
        var role = Role.Create("OPERATOR", "작업자");
        role.RoleName.Should().Be("작업자");
        role.Description.Should().BeEmpty();
    }

    [Fact]
    public void AddPermission_adds_unique_permissions()
    {
        var role = Role.Create("ADMIN", "관리자");
        role.AddPermission("MDM.WRITE");
        role.AddPermission("QMS.READ");
        role.Permissions.Should().HaveCount(2);
        role.Permissions.Should().Contain("MDM.WRITE");
        role.Permissions.Should().Contain("QMS.READ");
    }

    [Fact]
    public void AddPermission_duplicate_is_ignored()
    {
        var role = Role.Create("ADMIN", "관리자");
        role.AddPermission("MDM.WRITE");
        role.AddPermission("MDM.WRITE");
        role.Permissions.Should().HaveCount(1);
    }

    [Fact]
    public void RemovePermission_removes_existing_permission()
    {
        var role = Role.Create("ADMIN", "관리자");
        role.AddPermission("MDM.WRITE");
        role.AddPermission("QMS.READ");
        role.RemovePermission("MDM.WRITE");
        role.Permissions.Should().HaveCount(1);
        role.Permissions.Should().NotContain("MDM.WRITE");
    }

    [Fact]
    public void RemovePermission_nonexistent_does_not_throw()
    {
        var role = Role.Create("ADMIN", "관리자");
        var act = () => role.RemovePermission("NOT_EXISTS");
        act.Should().NotThrow();
        role.Permissions.Should().BeEmpty();
    }

    // ── MultiLanguageResource ─────────────────────────────────────────────────

    [Fact]
    public void Create_resource_valid_succeeds()
    {
        var res = MultiLanguageResource.Create("HOME.TITLE", "HOME", LanguageType.KoKr, "대시보드");
        res.Id.Should().Be("HOME.TITLE");
        res.MenuId.Should().Be("HOME");
        res.Language.Should().Be(LanguageType.KoKr);
        res.Value.Should().Be("대시보드");
    }

    [Fact]
    public void Create_resource_english_succeeds()
    {
        var res = MultiLanguageResource.Create("HOME.TITLE", "HOME", LanguageType.EnUs, "Dashboard");
        res.Language.Should().Be(LanguageType.EnUs);
        res.Value.Should().Be("Dashboard");
    }

    [Fact]
    public void UpdateValue_changes_value()
    {
        var res = MultiLanguageResource.Create("HOME.TITLE", "HOME", LanguageType.KoKr, "대시보드");
        res.UpdateValue("홈 화면");
        res.Value.Should().Be("홈 화면");
    }
}
