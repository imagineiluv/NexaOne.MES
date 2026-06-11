using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NexaOne.API.Controllers;
using NexaOne.API.Controllers.Models;
using NexaOne.API.Services;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Controllers;

/// <summary>§20.10 — 로그인 401 코드 구분(INVALID_CREDENTIALS/ACCOUNT_LOCKED),
/// Forgot 상태의 pwdChange 토큰 발급, 비밀번호 변경 성공 시 토큰 재발급을 검증한다.</summary>
public sealed class AuthControllerTests
{
    private const string Password = "pw123!";

    private static User UserWithPassword() =>
        User.Create("u001", "Alice", PasswordHasher.Hash(Password), "alice@test.com", "OPERATOR").Value;

    private static AuthController Build(
        Mock<IUserRepository> repo,
        Mock<IJwtService>? jwt = null,
        Mock<IRefreshTokenStore>? store = null,
        ClaimsPrincipal? principal = null)
    {
        jwt ??= new Mock<IJwtService>();
        store ??= new Mock<IRefreshTokenStore>();
        var userService = new UserService(
            repo.Object,
            new Mock<IRoleRepository>().Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            new Mock<ILoginFailureHistoryRepository>().Object);
        var resetService = new PasswordResetService(
            userService,
            new Mock<IEmailSender>().Object,
            new Mock<IMailTemplateService>().Object,
            NullLogger<PasswordResetService>.Instance);

        return new AuthController(jwt.Object, store.Object, userService, resetService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = principal ?? new ClaimsPrincipal() }
            }
        };
    }

    private static string? Prop(object? value, string name) =>
        value?.GetType().GetProperty(name)?.GetValue(value)?.ToString();

    // ── Login ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Login_valid_credentials_returns_tokens()
    {
        var user = UserWithPassword();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), false))
           .Returns("atk");
        var store = new Mock<IRefreshTokenStore>();
        store.Setup(s => s.IssueAsync("u001")).ReturnsAsync("rtk");

        var result = await Build(repo, jwt, store).Login(new LoginRequest("u001", Password), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<LoginResponse>().Subject;
        response.AccessToken.Should().Be("atk");
        response.RefreshToken.Should().Be("rtk");
        response.RequirePasswordChange.Should().BeFalse();
    }

    [Fact]
    public async Task Login_wrong_password_returns_401_INVALID_CREDENTIALS()
    {
        var user = UserWithPassword();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await Build(repo).Login(new LoginRequest("u001", "wrong"), default);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        Prop(unauthorized.Value, "code").Should().Be("INVALID_CREDENTIALS");
        Prop(unauthorized.Value, "message").Should().Be("Invalid credentials.",
            "자격 증명 오류 메시지는 계정 존재 여부를 드러내면 안 된다");
    }

    [Fact]
    public async Task Login_locked_account_returns_401_ACCOUNT_LOCKED_with_guidance()
    {
        var user = User.Restore(
            "u001", "Alice", PasswordHasher.Hash(Password), "alice@test.com", "OPERATOR", LanguageType.KoKr,
            isActive: true, isDeleted: false, deletedAt: null, lastLoginAt: null,
            passwordState: PasswordState.Normal,
            failCount: User.MaxConsecutiveFailures,
            lockedUntil: DateTime.UtcNow.AddMinutes(10));
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);

        var result = await Build(repo).Login(new LoginRequest("u001", Password), default);

        var unauthorized = result.Should().BeOfType<UnauthorizedObjectResult>().Subject;
        Prop(unauthorized.Value, "code").Should().Be("ACCOUNT_LOCKED");
        Prop(unauthorized.Value, "message").Should().Contain("잠겼습니다");
    }

    [Fact]
    public async Task Login_with_temporary_password_issues_pwdChange_token()
    {
        var user = UserWithPassword();
        user.SetTemporaryPassword(PasswordHasher.Hash("temp!"));
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), true))
           .Returns("atk-pwdchange");
        var store = new Mock<IRefreshTokenStore>();
        store.Setup(s => s.IssueAsync("u001")).ReturnsAsync("rtk");

        var result = await Build(repo, jwt, store).Login(new LoginRequest("u001", "temp!"), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var response = ok.Value.Should().BeOfType<LoginResponse>().Subject;
        response.RequirePasswordChange.Should().BeTrue();
        response.AccessToken.Should().Be("atk-pwdchange");
        jwt.Verify(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), true),
            Times.Once, "Forgot 상태에서는 pwdChange 클레임이 실린 토큰을 발급해야 한다");
    }

    // ── ChangePassword ────────────────────────────────────────────────────────

    private static ClaimsPrincipal PrincipalOf(string userId) =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, userId) }, "test"));

    [Fact]
    public async Task ChangePassword_success_reissues_tokens_without_pwdChange()
    {
        var user = UserWithPassword();
        user.SetTemporaryPassword(PasswordHasher.Hash("temp!"));
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), false))
           .Returns("newAtk");
        var store = new Mock<IRefreshTokenStore>();
        store.Setup(s => s.IssueAsync("u001")).ReturnsAsync("newRtk");

        var result = await Build(repo, jwt, store, PrincipalOf("u001"))
            .ChangePassword(new ChangePasswordRequest("temp!", "NewPw123!", "NewPw123!"), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        Prop(ok.Value, "accessToken").Should().Be("newAtk");
        Prop(ok.Value, "refreshToken").Should().Be("newRtk");
        user.PasswordState.Should().Be(PasswordState.Normal);
        jwt.Verify(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), false),
            Times.Once, "변경 후 토큰에는 pwdChange 클레임이 없어야 차단이 풀린다");
        store.Verify(s => s.RevokeAllByUserAsync("u001"),
            Times.Once, "§19.2.4-7 — 변경 성공 시 기존 리프레시 토큰을 모두 폐기해야 다른 기기 세션이 만료된다");
    }

    [Fact]
    public async Task ChangePassword_wrong_current_password_returns_400()
    {
        var user = UserWithPassword();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);

        var result = await Build(repo, principal: PrincipalOf("u001"))
            .ChangePassword(new ChangePasswordRequest("wrong", "NewPw123!", "NewPw123!"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ChangePassword_mismatched_confirm_returns_400()
    {
        var repo = new Mock<IUserRepository>();

        var result = await Build(repo, principal: PrincipalOf("u001"))
            .ChangePassword(new ChangePasswordRequest("cur", "NewPw123!", "different"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task ChangePassword_policy_violation_returns_400_with_code()
    {
        var user = UserWithPassword();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);

        // §19.2.2 — 8자 미만 + 대문자 없음 → 서버가 400으로 거부해야 한다
        var result = await Build(repo, principal: PrincipalOf("u001"))
            .ChangePassword(new ChangePasswordRequest(Password, "short1!", "short1!"), default);

        var bad = result.Should().BeOfType<BadRequestObjectResult>().Subject;
        Prop(bad.Value, "code").Should().Be("PASSWORD_POLICY_VIOLATION");
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_forgot_user_keeps_pwdChange_without_authorization_header()
    {
        var user = UserWithPassword();
        user.SetTemporaryPassword(PasswordHasher.Hash("temp!"));   // Forgot 상태
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), true))
           .Returns("atk-pwdchange");
        var store = new Mock<IRefreshTokenStore>();
        store.Setup(s => s.ValidateAsync("u001", "rtk")).ReturnsAsync(true);
        store.Setup(s => s.RotateAsync("u001", "rtk")).ReturnsAsync("rtk2");

        // Authorization 헤더 없이 호출 — 구 토큰 클레임 승계 방식이면 pwdChange가 소실된다
        var result = await Build(repo, jwt, store).Refresh(new RefreshRequest("u001", "rtk"), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        Prop(ok.Value, "accessToken").Should().Be("atk-pwdchange");
        Prop(ok.Value, "refreshToken").Should().Be("rtk2");
        jwt.Verify(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), true),
            Times.Once, "pwdChange는 헤더가 아니라 DB 상태로 재평가해야 우회가 안 된다");
    }

    [Fact]
    public async Task Refresh_normal_user_issues_token_without_pwdChange()
    {
        var user = UserWithPassword();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        var jwt = new Mock<IJwtService>();
        jwt.Setup(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), false))
           .Returns("atk");
        var store = new Mock<IRefreshTokenStore>();
        store.Setup(s => s.ValidateAsync("u001", "rtk")).ReturnsAsync(true);
        store.Setup(s => s.RotateAsync("u001", "rtk")).ReturnsAsync("rtk2");

        var result = await Build(repo, jwt, store).Refresh(new RefreshRequest("u001", "rtk"), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        Prop(ok.Value, "accessToken").Should().Be("atk");
        jwt.Verify(j => j.GenerateAccessToken("u001", "Alice", "DEFAULT", It.IsAny<IEnumerable<string>>(), false),
            Times.Once);
    }

    [Fact]
    public async Task Refresh_invalid_token_returns_401()
    {
        var repo = new Mock<IUserRepository>();
        var store = new Mock<IRefreshTokenStore>();
        store.Setup(s => s.ValidateAsync("u001", "bad")).ReturnsAsync(false);

        var result = await Build(repo, store: store).Refresh(new RefreshRequest("u001", "bad"), default);

        result.Should().BeOfType<UnauthorizedObjectResult>();
    }

    [Fact]
    public async Task Refresh_inactive_or_unknown_user_returns_401()
    {
        var inactive = User.Restore(
            "u001", "Alice", PasswordHasher.Hash(Password), "alice@test.com", "OPERATOR", LanguageType.KoKr,
            isActive: false, isDeleted: false, deletedAt: null, lastLoginAt: null,
            passwordState: PasswordState.Normal, failCount: 0, lockedUntil: null);
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(inactive);
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);
        var store = new Mock<IRefreshTokenStore>();
        store.Setup(s => s.ValidateAsync(It.IsAny<string>(), "rtk")).ReturnsAsync(true);

        (await Build(repo, store: store).Refresh(new RefreshRequest("u001", "rtk"), default))
            .Should().BeOfType<UnauthorizedObjectResult>("비활성 사용자는 토큰 갱신이 끊겨야 한다");
        (await Build(repo, store: store).Refresh(new RefreshRequest("ghost", "rtk"), default))
            .Should().BeOfType<UnauthorizedObjectResult>("존재하지 않는 사용자는 토큰 갱신이 끊겨야 한다");
    }

    // ── ForgotPassword ────────────────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_unknown_user_still_returns_202()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);

        var result = await Build(repo).ForgotPassword(new ForgotPasswordRequest("ghost", "x@test.com"), default);

        result.Should().BeOfType<AcceptedResult>("아이디/이메일이 틀려도 동일 응답이어야 계정 열거가 안 된다");
    }

    [Fact]
    public async Task ForgotPassword_valid_user_returns_same_202()
    {
        var user = UserWithPassword();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);

        var result = await Build(repo).ForgotPassword(new ForgotPasswordRequest("u001", "alice@test.com"), default);

        result.Should().BeOfType<AcceptedResult>();
    }

    // ── ResetPassword (구버전 호환) ───────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_delegates_to_forgot_flow_and_returns_202()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);

        var result = await Build(repo).ResetPassword(new ResetPasswordRequest("ghost", "x@test.com"), default);

        result.Should().BeOfType<AcceptedResult>(
            "301 리다이렉트는 POST→GET 변환으로 동작하지 않으므로 서버 내부 위임 후 동일 202를 반환해야 한다");
    }
}
