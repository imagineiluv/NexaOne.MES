using NexaOne.SYS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class UserTests
{
    [Fact]
    public void Create_user_with_valid_data_succeeds()
    {
        var result = User.Create("u001", "Alice", "hash", "alice@test.com", "ADMIN");
        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("u001");
        result.Value.UserName.Should().Be("Alice");
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_user_with_empty_id_fails()
    {
        var result = User.Create("", "Alice", "hash", "alice@test.com", "ADMIN");
        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("required");
    }

    [Fact]
    public void Deactivate_sets_IsDeleted()
    {
        var user = User.Create("u002", "Bob", "hash", "bob@test.com", "USER").Value;
        user.Deactivate();
        user.IsActive.Should().BeFalse();
        user.IsDeleted.Should().BeTrue();
        user.DeletedAt.Should().NotBeNull();
    }

    [Fact]
    public void ChangeLanguage_updates_language()
    {
        var user = User.Create("u003", "Charlie", "hash", "c@test.com", "USER").Value;
        user.ChangeLanguage(LanguageType.EnUs);
        user.Language.Should().Be(LanguageType.EnUs);
    }

    // ── §20.10 — 로그인 실패 잠금 ────────────────────────────────────────────

    private static User NewUser() =>
        User.Create("u010", "Dave", "hash", "d@test.com", "USER").Value;

    [Fact]
    public void RecordLoginFailure_under_threshold_does_not_lock()
    {
        var user = NewUser();
        var now = DateTime.UtcNow;

        for (var i = 0; i < User.MaxConsecutiveFailures - 1; i++)
            user.RecordLoginFailure(now).Should().BeFalse();

        user.FailCount.Should().Be(User.MaxConsecutiveFailures - 1);
        user.IsLockedAt(now).Should().BeFalse();
    }

    [Fact]
    public void RecordLoginFailure_fifth_failure_locks_for_30_minutes()
    {
        var user = NewUser();
        var now = DateTime.UtcNow;

        for (var i = 0; i < User.MaxConsecutiveFailures - 1; i++)
            user.RecordLoginFailure(now);
        var locked = user.RecordLoginFailure(now);

        locked.Should().BeTrue();
        user.LockedUntil.Should().Be(now.Add(User.LockDuration));
        user.IsLockedAt(now).Should().BeTrue();
        user.IsLockedAt(now.Add(User.LockDuration)).Should().BeFalse("잠금은 시간 만료로 해제된다");
    }

    [Fact]
    public void RecordLoginFailure_after_lock_expiry_restarts_count_from_one()
    {
        var user = NewUser();
        var now = DateTime.UtcNow;
        for (var i = 0; i < User.MaxConsecutiveFailures; i++)
            user.RecordLoginFailure(now);

        var afterExpiry = now.Add(User.LockDuration).AddMinutes(1);
        var locked = user.RecordLoginFailure(afterExpiry);

        locked.Should().BeFalse("만료된 잠금 이후 실패는 새 시도 구간으로 1회부터 다시 센다");
        user.FailCount.Should().Be(1);
        user.IsLockedAt(afterExpiry).Should().BeFalse();
    }

    [Fact]
    public void RecordLogin_resets_failure_counter()
    {
        var user = NewUser();
        user.RecordLoginFailure(DateTime.UtcNow);
        user.RecordLoginFailure(DateTime.UtcNow);

        user.RecordLogin();

        user.FailCount.Should().Be(0);
        user.LockedUntil.Should().BeNull();
        user.LastLoginAt.Should().NotBeNull();
    }

    [Fact]
    public void Unlock_clears_lock_and_counter()
    {
        var user = NewUser();
        var now = DateTime.UtcNow;
        for (var i = 0; i < User.MaxConsecutiveFailures; i++)
            user.RecordLoginFailure(now);

        user.Unlock();

        user.FailCount.Should().Be(0);
        user.IsLockedAt(now).Should().BeFalse();
    }

    // ── §20.10 — 비밀번호 상태 ──────────────────────────────────────────────

    [Fact]
    public void SetTemporaryPassword_sets_Forgot_state_and_requires_change()
    {
        var user = NewUser();
        user.SetTemporaryPassword("tempHash");

        user.PasswordHash.Should().Be("tempHash");
        user.PasswordState.Should().Be(PasswordState.Forgot);
        user.RequiresPasswordChange.Should().BeTrue();
    }

    [Fact]
    public void ChangePassword_restores_Normal_state()
    {
        var user = NewUser();
        user.SetTemporaryPassword("tempHash");

        user.ChangePassword("newHash");

        user.PasswordHash.Should().Be("newHash");
        user.PasswordState.Should().Be(PasswordState.Normal);
        user.RequiresPasswordChange.Should().BeFalse();
    }

    [Fact]
    public void Restore_rehydrates_full_persisted_state()
    {
        var lockedUntil = DateTime.UtcNow.AddMinutes(10);
        var user = User.Restore(
            "u011", "Eve", "hash", "e@test.com", "USER", LanguageType.EnUs,
            isActive: false, isDeleted: true, deletedAt: DateTime.UtcNow,
            lastLoginAt: DateTime.UtcNow.AddDays(-1),
            passwordState: PasswordState.Forgot, failCount: 3, lockedUntil: lockedUntil);

        user.IsActive.Should().BeFalse("비활성 상태가 복원돼야 로그인 차단이 동작한다");
        user.IsDeleted.Should().BeTrue();
        user.PasswordState.Should().Be(PasswordState.Forgot);
        user.FailCount.Should().Be(3);
        user.LockedUntil.Should().Be(lockedUntil);
    }
}
