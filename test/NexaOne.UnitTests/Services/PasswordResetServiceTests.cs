using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NexaOne.API.Services;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Services;

/// <summary>§20.10 — 비밀번호 분실 처리. 계정 열거 방지(불일치 시 무동작),
/// 임시 비밀번호 즉시 해시 저장 + Forgot 상태, 템플릿 치환/평문 폴백을 검증한다.</summary>
public sealed class PasswordResetServiceTests
{
    private static User ActiveUser() =>
        User.Create("u001", "Alice", PasswordHasher.Hash("original"), "alice@test.com", "OPERATOR").Value;

    private static PasswordResetService Build(
        Mock<IUserRepository> repo,
        Mock<IEmailSender>? email = null,
        Mock<IMailTemplateService>? template = null)
    {
        var userService = new UserService(
            repo.Object,
            new Mock<IRoleRepository>().Object,
            new Mock<IMultiLanguageResourceRepository>().Object,
            new Mock<ILoginFailureHistoryRepository>().Object);
        return new PasswordResetService(
            userService,
            (email ?? new Mock<IEmailSender>()).Object,
            (template ?? new Mock<IMailTemplateService>()).Object,
            NullLogger<PasswordResetService>.Instance);
    }

    [Fact]
    public async Task ForgotPassword_unknown_user_sends_no_email()
    {
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("ghost", default)).ReturnsAsync((User?)null);
        var email = new Mock<IEmailSender>();

        await Build(repo, email).ForgotPasswordAsync("ghost", "any@test.com");

        email.Verify(e => e.SendAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default), Times.Never);
        repo.Verify(r => r.UpdateAsync(It.IsAny<User>(), default), Times.Never);
    }

    [Fact]
    public async Task ForgotPassword_email_mismatch_sends_no_email_and_keeps_password()
    {
        var user = ActiveUser();
        var originalHash = user.PasswordHash;
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        var email = new Mock<IEmailSender>();

        await Build(repo, email).ForgotPasswordAsync("u001", "other@test.com");

        email.Verify(e => e.SendAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default), Times.Never);
        user.PasswordHash.Should().Be(originalHash, "이메일 불일치 시 비밀번호를 건드리면 안 된다");
        user.PasswordState.Should().Be(PasswordState.Normal);
    }

    [Fact]
    public async Task ForgotPassword_valid_request_sets_Forgot_state_and_sends_email()
    {
        var user = ActiveUser();
        var originalHash = user.PasswordHash;
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var email = new Mock<IEmailSender>();

        await Build(repo, email).ForgotPasswordAsync("u001", "alice@test.com");

        user.PasswordState.Should().Be(PasswordState.Forgot);
        user.PasswordHash.Should().NotBe(originalHash);
        user.PasswordHash.Should().MatchRegex("^[0-9a-f]{64}$", "임시 비밀번호는 즉시 SHA-256 해시로 저장된다");
        repo.Verify(r => r.UpdateAsync(user, default), Times.Once);
        email.Verify(e => e.SendAsync(
            "alice@test.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_email_comparison_is_case_insensitive()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var email = new Mock<IEmailSender>();

        await Build(repo, email).ForgotPasswordAsync("u001", "ALICE@TEST.COM");

        email.Verify(e => e.SendAsync(
            "alice@test.com", It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_emailed_password_matches_stored_hash()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var template = new Mock<IMailTemplateService>();
        string? mailedPassword = null;
        template
            .Setup(t => t.Render("InitPassword", "ko-KR", It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, string, IReadOnlyDictionary<string, string>>((_, _, v) => mailedPassword = v["PASSWORD"])
            .Returns("<html>rendered</html>");

        await Build(repo, template: template).ForgotPasswordAsync("u001", "alice@test.com");

        mailedPassword.Should().NotBeNullOrEmpty();
        PasswordHasher.Hash(mailedPassword!).Should().Be(
            user.PasswordHash, "메일로 보낸 임시 비밀번호 원문과 저장된 해시가 일치해야 로그인이 가능하다");
    }

    [Fact]
    public async Task ForgotPassword_rendered_template_is_sent_as_html()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var email = new Mock<IEmailSender>();
        var template = new Mock<IMailTemplateService>();
        template
            .Setup(t => t.Render("InitPassword", "ko-KR", It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Returns("<html>rendered</html>");

        await Build(repo, email, template).ForgotPasswordAsync("u001", "alice@test.com");

        email.Verify(e => e.SendAsync(
            "alice@test.com", It.IsAny<string>(), "<html>rendered</html>", true, default), Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_without_template_falls_back_to_plaintext()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var email = new Mock<IEmailSender>();
        var template = new Mock<IMailTemplateService>();
        template
            .Setup(t => t.Render(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Returns((string?)null);

        await Build(repo, email, template).ForgotPasswordAsync("u001", "alice@test.com");

        email.Verify(e => e.SendAsync(
            "alice@test.com", It.IsAny<string>(), It.Is<string>(b => b.Contains("임시 비밀번호")), false, default),
            Times.Once);
    }

    [Fact]
    public async Task ForgotPassword_email_send_failure_does_not_throw()
    {
        var user = ActiveUser();
        var repo = new Mock<IUserRepository>();
        repo.Setup(r => r.GetByIdAsync("u001", default)).ReturnsAsync(user);
        repo.Setup(r => r.UpdateAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        var email = new Mock<IEmailSender>();
        email.Setup(e => e.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default))
            .ThrowsAsync(new InvalidOperationException("SMTP down"));

        var act = () => Build(repo, email).ForgotPasswordAsync("u001", "alice@test.com");

        await act.Should().NotThrowAsync("메일 발송 실패는 로그만 남기고 API 응답(202)은 동일해야 한다");
    }
}
