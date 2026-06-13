using Moq;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class UserServiceTests
{
    // pw는 평문 — 저장 해시는 PasswordHasher.Hash(pw)(PBKDF2). 로그인은 평문 pw로 검증한다.
    private static User ActiveUser(string id = "u001", string pw = "hash123") =>
        User.Create(id, "Alice", PasswordHasher.Hash(pw), "alice@test.com", "OPERATOR").Value;

    private static UserService BuildService(
        Mock<IUserRepository> repo,
        Mock<ILoginFailureHistoryRepository>? failureRepo = null) =>
        new(repo.Object,
            new Mock<IRoleRepository>().Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            (failureRepo ?? new Mock<ILoginFailureHistoryRepository>()).Object);

    // ── CreateUserAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateUser_new_id_succeeds()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.ExistsAsync("u001", default)).ReturnsAsync(false);
        repo.Setup(r => r.AddAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreateUserAsync("u001", "Alice", "hash", "a@a.com", "OPERATOR");

        result.IsSuccess.Should().BeTrue();
        result.Value.Id.Should().Be("u001");
        repo.Verify(r => r.AddAsync(It.IsAny<User>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateUser_duplicate_id_returns_conflict()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.ExistsAsync("u001", default)).ReturnsAsync(true);

        var result = await BuildService(repo).CreateUserAsync("u001", "Alice", "hash", "a@a.com", "OPERATOR");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("already exists");
    }

    // ── ValidateAndLoginAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAndLogin_correct_credentials_succeeds()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).ValidateAndLoginAsync("u001", "hash123");

        result.IsSuccess.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<User>(), default), Times.Once);
    }

    [Fact]
    public async Task ValidateAndLogin_wrong_password_fails()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);

        var result = await BuildService(repo).ValidateAndLoginAsync("u001", "wrong");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.InvalidCredentials");
    }

    [Fact]
    public async Task ValidateAndLogin_unknown_user_fails()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);

        var result = await BuildService(repo).ValidateAndLoginAsync("ghost", "hash");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAndLogin_inactive_user_fails()
    {
        var user = ActiveUser();
        user.Deactivate();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);

        var result = await BuildService(repo).ValidateAndLoginAsync("u001", "hash123");

        result.IsFailure.Should().BeTrue();
    }

    // ── ValidateAndLoginAsync — §20.10 잠금/실패 이력 ─────────────────────────

    [Fact]
    public async Task ValidateAndLogin_unknown_user_records_UserNotFound_history()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);
        var failureRepo = new Mock<ILoginFailureHistoryRepository>();

        await BuildService(repo, failureRepo).ValidateAndLoginAsync("ghost", "hash", "10.0.0.1", "agent");

        failureRepo.Verify(f => f.AddAsync(
            It.Is<LoginFailureHistory>(h =>
                h.UserId == "ghost" &&
                h.IpAddress == "10.0.0.1" &&
                h.UserAgent == "agent" &&
                h.FailureReason == LoginFailureHistory.Reasons.UserNotFound),
            default), Times.Once);
    }

    [Fact]
    public async Task ValidateAndLogin_wrong_password_records_WrongPassword_history_and_increments_count()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.RecordLoginFailureAsync("u001", It.IsAny<DateTime>(), default))
            .ReturnsAsync((DateTime?)null);
        var failureRepo = new Mock<ILoginFailureHistoryRepository>();

        var result = await BuildService(repo, failureRepo).ValidateAndLoginAsync("u001", "wrong", "10.0.0.1", "agent");

        result.Error.Code.Should().Be("Auth.InvalidCredentials");
        repo.Verify(r => r.RecordLoginFailureAsync("u001", It.IsAny<DateTime>(), default), Times.Once,
            "실패 카운터는 원자 UPDATE로 DB에 영속화돼야 한다 (동시 실패 시 증가 유실 방지)");
        failureRepo.Verify(f => f.AddAsync(
            It.Is<LoginFailureHistory>(h => h.FailureReason == LoginFailureHistory.Reasons.WrongPassword),
            default), Times.Once);
    }

    [Fact]
    public async Task ValidateAndLogin_failure_reaching_threshold_returns_AccountLocked()
    {
        // 임계 도달/잠금 판정 자체는 원자 UPDATE(리포지토리)와 도메인 RecordLoginFailure가 맡는다 —
        // 여기서는 리포지토리가 잠금 만료 시각을 반환하면 ACCOUNT_LOCKED 오류로 매핑되는지 검증한다.
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.RecordLoginFailureAsync("u001", It.IsAny<DateTime>(), default))
            .ReturnsAsync(DateTime.UtcNow.Add(User.LockDuration));
        var failureRepo = new Mock<ILoginFailureHistoryRepository>();

        var result = await BuildService(repo, failureRepo).ValidateAndLoginAsync("u001", "wrong", "10.0.0.1", "agent");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.AccountLocked");
        result.Error.Description.Should().Contain("잠겼습니다");
    }

    [Fact]
    public async Task ValidateAndLogin_locked_user_fails_even_with_correct_password()
    {
        var user = User.Restore(
            "u001", "Alice", "hash123", "alice@test.com", "OPERATOR", LanguageType.KoKr,
            isActive: true, isDeleted: false, deletedAt: null, lastLoginAt: null,
            passwordState: PasswordState.Normal,
            failCount: User.MaxConsecutiveFailures,
            lockedUntil: DateTime.UtcNow.AddMinutes(10));
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        var failureRepo = new Mock<ILoginFailureHistoryRepository>();

        var result = await BuildService(repo, failureRepo).ValidateAndLoginAsync("u001", "hash123", "ip", "agent");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.AccountLocked");
        failureRepo.Verify(f => f.AddAsync(
            It.Is<LoginFailureHistory>(h => h.FailureReason == LoginFailureHistory.Reasons.AccountLocked),
            default), Times.Once);
    }

    [Fact]
    public async Task ValidateAndLogin_inactive_user_records_InactiveUser_history()
    {
        var user = ActiveUser();
        user.Deactivate();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        var failureRepo = new Mock<ILoginFailureHistoryRepository>();

        var result = await BuildService(repo, failureRepo).ValidateAndLoginAsync("u001", "hash123");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.InvalidCredentials");
        failureRepo.Verify(f => f.AddAsync(
            It.Is<LoginFailureHistory>(h => h.FailureReason == LoginFailureHistory.Reasons.InactiveUser),
            default), Times.Once);
    }

    [Fact]
    public async Task ValidateAndLogin_success_after_failures_resets_counter()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var service = BuildService(repo);

        await service.ValidateAndLoginAsync("u001", "wrong");
        await service.ValidateAndLoginAsync("u001", "wrong");
        var result = await service.ValidateAndLoginAsync("u001", "hash123");

        result.IsSuccess.Should().BeTrue();
        user.FailCount.Should().Be(0, "성공 로그인은 연속 실패 카운터를 끊는다");
    }

    [Fact]
    public async Task UnlockUser_clears_lock()
    {
        var user = User.Restore(
            "u001", "Alice", "hash123", "alice@test.com", "OPERATOR", LanguageType.KoKr,
            isActive: true, isDeleted: false, deletedAt: null, lastLoginAt: null,
            passwordState: PasswordState.Normal,
            failCount: User.MaxConsecutiveFailures,
            lockedUntil: DateTime.UtcNow.AddMinutes(10));
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).UnlockUserAsync("u001");

        result.IsSuccess.Should().BeTrue();
        user.IsLockedAt(DateTime.UtcNow).Should().BeFalse();
        repo.Verify(r => r.UpdateAsync(user, default), Times.Once);
    }

    // ── ForceSetPasswordAsync — §20.10 임시 비밀번호 ──────────────────────────

    [Fact]
    public async Task ForceSetPassword_sets_Forgot_state()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).ForceSetPasswordAsync("u001", "tempHash");

        result.IsSuccess.Should().BeTrue();
        user.PasswordHash.Should().Be("tempHash");
        user.PasswordState.Should().Be(PasswordState.Forgot);
        user.RequiresPasswordChange.Should().BeTrue();
    }

    // ── GetEffectivePermissionsAsync (ADR-003) ────────────────────────────────

    [Fact]
    public async Task GetEffectivePermissions_unions_role_defaults_and_explicit()
    {
        var role = Role.Create("OPERATOR", "Operator");
        role.AddPermission("mdm:manage");   // 명시 권한
        var repo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        roleRepo.Setup(r => r.GetByIdAsync("OPERATOR", default)).ReturnsAsync(role);
        var svc = new UserService(repo.Object, roleRepo.Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            new Mock<ILoginFailureHistoryRepository>().Object);

        var perms = await svc.GetEffectivePermissionsAsync("OPERATOR");

        perms.Should().Contain("fdc:control", "OPERATOR 기본 매핑(하위호환 시드) 포함");
        perms.Should().Contain("mdm:manage", "역할의 명시 권한과 합집합");
    }

    [Fact]
    public async Task GetEffectivePermissions_admin_has_wildcard_even_without_db_role()
    {
        var repo = new Mock<IUserRepository>();
        var roleRepo = new Mock<IRoleRepository>();
        roleRepo.Setup(r => r.GetByIdAsync("ADMIN", default)).ReturnsAsync((Role?)null);
        var svc = new UserService(repo.Object, roleRepo.Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            new Mock<ILoginFailureHistoryRepository>().Object);

        var perms = await svc.GetEffectivePermissionsAsync("ADMIN");

        perms.Should().Contain("*", "ADMIN 기본 매핑은 전체 권한(*)을 포함");
    }

    // ── ChangePasswordAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_correct_current_password_succeeds()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).ChangePasswordAsync("u001", "hash123", "newHash");

        result.IsSuccess.Should().BeTrue();
        PasswordHasher.Verify("newHash", user.PasswordHash).Should().BeTrue("새 비밀번호가 강화 해시로 저장된다");
    }

    [Fact]
    public async Task ValidateAndLogin_rehashes_legacy_sha256_on_success()
    {
        // 구 무염 SHA-256 hex로 저장된 사용자 — 원문으로 검증되고 로그인 성공 시 강화 해시로 교체되어야 한다
        var legacy = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("secret")))
            .ToLowerInvariant();
        var user = User.Create("u001", "Alice", legacy, "alice@test.com", "OPERATOR").Value;
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        User? saved = null;
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default))
            .Callback<User, CancellationToken>((u, _) => saved = u)
            .Returns(Task.CompletedTask);

        var result = await BuildService(repo).ValidateAndLoginAsync("u001", "secret");

        result.IsSuccess.Should().BeTrue("구 SHA-256 해시도 원문으로 검증된다");
        saved.Should().NotBeNull();
        PasswordHasher.NeedsRehash(saved!.PasswordHash).Should().BeFalse("로그인 성공 시 강화 해시로 재해싱된다");
        PasswordHasher.Verify("secret", saved.PasswordHash).Should().BeTrue();
    }

    [Fact]
    public async Task ChangePassword_wrong_current_password_fails()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);

        var result = await BuildService(repo).ChangePasswordAsync("u001", "wrongHash", "newHash");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Auth.WrongPassword");
    }

    [Fact]
    public async Task ChangePassword_unknown_user_returns_not_found()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);

        var result = await BuildService(repo).ChangePasswordAsync("ghost", "hash", "newHash");

        result.IsFailure.Should().BeTrue();
    }

    // ── DeactivateUserAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task DeactivateUser_active_user_sets_inactive()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).DeactivateUserAsync("u001");

        result.IsSuccess.Should().BeTrue();
        user.IsActive.Should().BeFalse();
    }
}
