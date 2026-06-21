using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>Role 애그리거트 — 읽기경로 Restore가 권한·소프트삭제·감사 메타데이터를 검증 없이 그대로 복원함을 검증한다.</summary>
public sealed class RoleTests
{
    [Fact]
    public void Create_initializes_role_without_permissions_or_delete_flag()
    {
        var role = Role.Create("ROLE-ADMIN", "관리자", "전체 권한");

        role.Id.Should().Be("ROLE-ADMIN");
        role.RoleName.Should().Be("관리자");
        role.Description.Should().Be("전체 권한");
        role.Permissions.Should().BeEmpty();
        role.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public void Restore_preserves_permissions_softdelete_and_audit_without_revalidation()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updated = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);

        var role = Role.Restore(
            "ROLE-OPS", "운영자", "라인 운영",
            permissions: new[] { "POM.READ", "POM.WRITE", "RMS.APPROVE" },
            isDeleted: true,
            createdBy: "seeder", createdAt: created, updatedBy: "editor", updatedAt: updated);

        role.RoleName.Should().Be("운영자");
        role.Description.Should().Be("라인 운영");
        role.Permissions.Should().Equal("POM.READ", "POM.WRITE", "RMS.APPROVE");   // 영속된 권한 목록을 순서대로 복원
        role.IsDeleted.Should().BeTrue("영속된 소프트삭제 상태가 비삭제로 둔갑하면 안 된다");
        role.CreatedBy.Should().Be("seeder", "감사 메타데이터 보존(매 읽기 UtcNow/\"\" 리셋 없음)");
        role.CreatedAt.Should().Be(created);
        role.UpdatedBy.Should().Be("editor");
        role.UpdatedAt.Should().Be(updated);
    }

    [Fact]
    public void Restore_deduplicates_permissions()
    {
        var role = Role.Restore("ROLE-DUP", "역할", "",
            permissions: new[] { "P1", "P1", "P2" }, isDeleted: false);

        role.Permissions.Should().BeEquivalentTo("P1", "P2");
    }
}
