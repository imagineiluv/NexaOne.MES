using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>설계서 §20.10 — 로그인 실패 이력 도메인. 읽기경로 Restore가 절단/검증 없이
/// 영속값과 감사 메타데이터를 그대로 복원함을 검증한다.</summary>
public sealed class LoginFailureHistoryTests
{
    private static readonly DateTime Occurred = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Create_truncates_overlong_fields()
    {
        var longAgent = new string('x', 600);
        var history = LoginFailureHistory.Create("user1", "10.0.0.1", longAgent,
            LoginFailureHistory.Reasons.WrongPassword, Occurred);

        history.UserAgent.Length.Should().Be(500, "Create는 컬럼 길이에 맞춰 절단한다 (§20.10)");
        history.FailureReason.Should().Be(LoginFailureHistory.Reasons.WrongPassword);
    }

    [Fact]
    public void Restore_preserves_persisted_values_and_audit_without_truncation()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updated = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var longAgent = new string('x', 600);   // Create라면 500자로 절단되지만 Restore는 영속값을 그대로 보존

        var history = LoginFailureHistory.Restore(
            "FAIL-1", "user1", "10.0.0.1", longAgent,
            LoginFailureHistory.Reasons.AccountLocked, Occurred,
            createdBy: "seeder", createdAt: created, updatedBy: "editor", updatedAt: updated);

        history.Id.Should().Be("FAIL-1");
        history.UserId.Should().Be("user1");
        history.UserAgent.Should().Be(longAgent, "Restore는 절단 없이 영속값을 그대로 보존한다");
        history.FailureReason.Should().Be(LoginFailureHistory.Reasons.AccountLocked);
        history.OccurredAt.Should().Be(Occurred);
        history.CreatedBy.Should().Be("seeder", "감사 메타데이터 보존(매 읽기 UtcNow/\"\" 리셋 없음)");
        history.CreatedAt.Should().Be(created);
        history.UpdatedBy.Should().Be("editor");
        history.UpdatedAt.Should().Be(updated);
    }
}
