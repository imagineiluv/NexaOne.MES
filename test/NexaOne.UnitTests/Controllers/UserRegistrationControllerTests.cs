using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NexaOne.API.Controllers;
using NexaOne.API.Services;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Controllers;

/// <summary>
/// §19.3 — UserRegistrationController: 인증 사용자(NameIdentifier)가 승인자/반려자로 흘러가는지,
/// 임시 비밀번호 메일 발송(원문-해시 일치), 관리자 알림 설정 분기, HTTP 응답 코드를 검증한다.
/// </summary>
public sealed class UserRegistrationControllerTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUserRequestRepository> _requests = new();
    private readonly Mock<IUserRepository> _users = new();
    private readonly Mock<IEmailSender> _email = new();
    private readonly Mock<IMailTemplateService> _template = new();

    public UserRegistrationControllerTests()
    {
        _users.Setup(u => u.ExistsAsync(It.IsAny<string>(), default)).ReturnsAsync(false);
        _users.Setup(u => u.AddAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        _requests.Setup(r => r.GetByUserIdAsync(It.IsAny<string>(), default)).ReturnsAsync((UserRequest?)null);
        _requests.Setup(r => r.AddAsync(It.IsAny<UserRequest>(), default)).Returns(Task.CompletedTask);
        _requests.Setup(r => r.UpdateAsync(It.IsAny<UserRequest>(), default)).Returns(Task.CompletedTask);
        _template.Setup(t => t.Render(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Returns((string?)null);   // 기본은 평문 폴백
    }

    private UserRegistrationController BuildController(Dictionary<string, string?>? settings = null)
    {
        var registration = new UserRegistrationService(_requests.Object, _users.Object);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings ?? new Dictionary<string, string?>())
            .Build();
        var approval = new UserApprovalService(
            registration, _email.Object, _template.Object, configuration,
            NullLogger<UserApprovalService>.Instance);
        return new UserRegistrationController(approval, registration)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        new[] { new Claim(ClaimTypes.NameIdentifier, "worker1") }, "test"))
                }
            }
        };
    }

    private static UserRegistrationRequest RegistrationRequest(
        string userId = "newbie", bool termsAccepted = true, string? language = "KoKr") =>
        new(userId, "홍길동", "new@test.com", "생산팀", "사원", "P1", language, termsAccepted);

    private static UserRequest PendingRequest(string requestId = "REQ1", string userId = "newbie") =>
        UserRequest.Create(requestId, userId, "홍길동", "new@test.com", "생산팀", "사원",
            "P1", LanguageType.KoKr, termsAccepted: true, Now, "10.0.0.1").Value;

    // ── CheckUserId / SubmitRequest ───────────────────────────────────────────

    [Fact]
    public async Task CheckUserId_returns_200_with_availability()
    {
        var result = await BuildController().CheckUserId("fresh", default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.GetType().GetProperty("available")!.GetValue(ok.Value).Should().Be(true);
    }

    [Fact]
    public async Task SubmitRequest_valid_returns_200_with_dto()
    {
        var result = await BuildController().SubmitRequest(RegistrationRequest(), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<UserRequestDto>().Subject;
        dto.UserId.Should().Be("newbie");
        dto.Status.Should().Be("Request");
        dto.RequestVersion.Should().Be(1);
        _requests.Verify(r => r.AddAsync(It.IsAny<UserRequest>(), default), Times.Once);
    }

    [Fact]
    public async Task SubmitRequest_without_terms_returns_400()
    {
        var result = await BuildController().SubmitRequest(
            RegistrationRequest(termsAccepted: false), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        _requests.Verify(r => r.AddAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Theory]
    [InlineData("Klingon")]   // 미정의 이름 — TryParse 실패
    [InlineData("999")]       // TryParse는 숫자 문자열을 미정의 enum 값으로 통과시킨다 — Enum.IsDefined로 차단
    public async Task SubmitRequest_unknown_language_falls_back_to_korean(string language)
    {
        var result = await BuildController().SubmitRequest(
            RegistrationRequest(language: language), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeOfType<UserRequestDto>().Which.Language.Should().Be("KoKr");
    }

    [Fact]
    public async Task SubmitRequest_sends_admin_mail_only_when_configured()
    {
        var settings = new Dictionary<string, string?> { ["Registration:AdminEmail"] = "admin@test.com" };

        await BuildController(settings).SubmitRequest(RegistrationRequest(), default);

        _email.Verify(e => e.SendAsync(
            "admin@test.com", It.IsAny<string>(), It.IsAny<string>(), false, default), Times.Once);
    }

    [Fact]
    public async Task SubmitRequest_without_admin_config_sends_no_mail()
    {
        await BuildController().SubmitRequest(RegistrationRequest(), default);

        _email.Verify(e => e.SendAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default), Times.Never);
    }

    // ── GetRequests / GetRequest ──────────────────────────────────────────────

    [Fact]
    public async Task GetRequests_returns_200_with_list()
    {
        _requests.Setup(r => r.SearchAsync(
                "P1", "Request", null, null, null, null, null, default))
            .ReturnsAsync(new List<UserRequest> { PendingRequest() });

        var result = await BuildController().GetRequests("P1", "Request", null, null, null, null, null, default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IEnumerable<UserRequestDto>>().Which.Should().ContainSingle();
    }

    [Fact]
    public async Task GetRequests_invalid_period_returns_400()
    {
        var result = await BuildController().GetRequests(
            null, null, null, null, null, Now.AddDays(1), Now, default);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task GetRequest_unknown_returns_404()
    {
        _requests.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((UserRequest?)null);

        var result = await BuildController().GetRequest("RXXX", default);

        result.Should().BeOfType<NotFoundObjectResult>();
    }

    // ── Approve ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_returns_200_and_mails_temp_password_matching_stored_hash()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());
        User? created = null;
        _users.Setup(u => u.AddAsync(It.IsAny<User>(), default))
            .Callback<User, CancellationToken>((u, _) => created = u)
            .Returns(Task.CompletedTask);
        string? mailedPassword = null;
        _template.Setup(t => t.Render("InitPassword", "ko-KR", It.IsAny<IReadOnlyDictionary<string, string>>()))
            .Callback<string, string, IReadOnlyDictionary<string, string>>((_, _, v) => mailedPassword = v["PASSWORD"])
            .Returns("<html>rendered</html>");

        var result = await BuildController().Approve(
            "REQ1", new ApproveUserRequestRequest("OPERATOR"), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<UserRequestDto>().Subject;
        dto.Status.Should().Be("Approved");
        dto.ApprovedBy.Should().Be("worker1", "인증 토큰의 사용자가 승인자로 기록된다");

        created.Should().NotBeNull();
        created!.RoleId.Should().Be("OPERATOR");
        created.PasswordState.Should().Be(PasswordState.Create);
        mailedPassword.Should().NotBeNullOrEmpty();
        PasswordHasher.Hash(mailedPassword!).Should().Be(
            created.PasswordHash, "메일로 보낸 임시 비밀번호 원문과 저장된 해시가 일치해야 로그인이 가능하다");
        _email.Verify(e => e.SendAsync(
            "new@test.com", It.IsAny<string>(), "<html>rendered</html>", true, default), Times.Once);
    }

    [Fact]
    public async Task Approve_null_role_defaults_to_USER()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());
        User? created = null;
        _users.Setup(u => u.AddAsync(It.IsAny<User>(), default))
            .Callback<User, CancellationToken>((u, _) => created = u)
            .Returns(Task.CompletedTask);

        var result = await BuildController().Approve(
            "REQ1", new ApproveUserRequestRequest(null), default);

        result.Should().BeOfType<OkObjectResult>();
        created!.RoleId.Should().Be("USER");
    }

    [Fact]
    public async Task Approve_already_processed_returns_400()
    {
        var request = PendingRequest();
        request.Approve("admin", Now);
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(request);

        var result = await BuildController().Approve(
            "REQ1", new ApproveUserRequestRequest("USER"), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        _users.Verify(u => u.AddAsync(It.IsAny<User>(), default), Times.Never);
        _email.Verify(e => e.SendAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default), Times.Never);
    }

    [Fact]
    public async Task Approve_mail_failure_still_returns_200()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());
        _email.Setup(e => e.SendAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default))
            .ThrowsAsync(new InvalidOperationException("SMTP down"));

        var result = await BuildController().Approve(
            "REQ1", new ApproveUserRequestRequest("USER"), default);

        result.Should().BeOfType<OkObjectResult>("메일 실패는 로그만 남기고 승인 자체는 완료되어야 한다");
    }

    // ── Reject ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_returns_200_and_records_reason()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());

        var result = await BuildController().Reject(
            "REQ1", new RejectUserRequestRequest("서류 미비"), default);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var dto = ok.Value.Should().BeOfType<UserRequestDto>().Subject;
        dto.Status.Should().Be("Rejected");
        dto.RejectReason.Should().Be("서류 미비");
        dto.RejectedBy.Should().Be("worker1", "인증 토큰의 사용자가 반려자로 기록된다");
        _email.Verify(e => e.SendAsync(
            "new@test.com", It.IsAny<string>(), It.Is<string>(b => b.Contains("서류 미비")), false, default),
            Times.Once);
    }

    [Fact]
    public async Task Reject_without_reason_returns_400()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());

        var result = await BuildController().Reject(
            "REQ1", new RejectUserRequestRequest(null), default);

        result.Should().BeOfType<BadRequestObjectResult>();
        _requests.Verify(r => r.UpdateAsync(It.IsAny<UserRequest>(), default), Times.Never);
        _email.Verify(e => e.SendAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), default), Times.Never);
    }
}
