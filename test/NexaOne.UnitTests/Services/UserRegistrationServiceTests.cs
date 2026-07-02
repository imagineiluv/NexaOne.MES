using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Services;

/// <summary>§19.3 — 등록 신청/승인 서비스. 아이디 중복 판정, 반려 후 재신청(같은 행 + version 증가),
/// 승인 시 사용자 생성(PasswordState=Create, 기본 역할 USER), 충돌 시 사용자 미생성을 검증한다.</summary>
public sealed class UserRegistrationServiceTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private readonly Mock<IUserRequestRepository> _requests = new();
    private readonly Mock<IUserRepository> _users = new();

    public UserRegistrationServiceTests()
    {
        _users.Setup(u => u.ExistsAsync(It.IsAny<string>(), default)).ReturnsAsync(false);
        _users.Setup(u => u.AddAsync(It.IsAny<User>(), default)).Returns(Task.CompletedTask);
        _requests.Setup(r => r.GetByUserIdAsync(It.IsAny<string>(), default)).ReturnsAsync((UserRequest?)null);
        _requests.Setup(r => r.AddAsync(It.IsAny<UserRequest>(), default)).Returns(Task.CompletedTask);
        _requests.Setup(r => r.UpdateAsync(It.IsAny<UserRequest>(), default)).Returns(Task.CompletedTask);
    }

    private UserRegistrationService Build() => new(_requests.Object, _users.Object);

    private static UserRegistrationCommand Command(string userId = "newbie", bool termsAccepted = true) =>
        new(userId, "홍길동", "new@test.com", "생산팀", "사원", "P1",
            LanguageType.KoKr, termsAccepted, "10.0.0.1");

    private static UserRequest PendingRequest(string requestId = "REQ1", string userId = "newbie") =>
        UserRequest.Create(requestId, userId, "홍길동", "new@test.com", "생산팀", "사원",
            "P1", LanguageType.KoKr, termsAccepted: true, Now, "10.0.0.1").Value;

    private static UserRequest RejectedRequest(string requestId = "REQ1", string userId = "newbie")
    {
        var request = PendingRequest(requestId, userId);
        request.Reject("admin", "부서 확인 불가", Now.AddHours(1));
        return request;
    }

    // ── IsUserIdAvailableAsync ────────────────────────────────────────────────

    [Fact]
    public async Task IsUserIdAvailable_blank_returns_false()
    {
        (await Build().IsUserIdAvailableAsync("   ")).Should().BeFalse();
    }

    [Fact]
    public async Task IsUserIdAvailable_existing_user_returns_false()
    {
        _users.Setup(u => u.ExistsAsync("newbie", default)).ReturnsAsync(true);

        (await Build().IsUserIdAvailableAsync("newbie")).Should().BeFalse();
    }

    [Fact]
    public async Task IsUserIdAvailable_no_request_returns_true()
    {
        (await Build().IsUserIdAvailableAsync("newbie")).Should().BeTrue();
    }

    [Fact]
    public async Task IsUserIdAvailable_pending_request_returns_false()
    {
        _requests.Setup(r => r.GetByUserIdAsync("newbie", default)).ReturnsAsync(PendingRequest());

        (await Build().IsUserIdAvailableAsync("newbie")).Should().BeFalse();
    }

    [Fact]
    public async Task IsUserIdAvailable_rejected_request_returns_true()
    {
        _requests.Setup(r => r.GetByUserIdAsync("newbie", default)).ReturnsAsync(RejectedRequest());

        (await Build().IsUserIdAvailableAsync("newbie"))
            .Should().BeTrue("반려된 신청은 같은 ID로 재신청이 가능하다 (§19.3.6)");
    }

    // ── RequestAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Request_new_user_adds_request()
    {
        var result = await Build().RequestAsync(Command(), Now);

        result.IsSuccess.Should().BeTrue();
        result.Value.UserId.Should().Be("newbie");
        result.Value.RequestVersion.Should().Be(1);
        result.Value.Status.Should().Be(UserRequestStatus.Request);
        _requests.Verify(r => r.AddAsync(result.Value, default), Times.Once);
        _requests.Verify(r => r.UpdateAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Request_blank_user_id_fails()
    {
        var result = await Build().RequestAsync(Command(userId: "  "), Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UserRequest.UserIdRequired");
        _requests.Verify(r => r.AddAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Request_existing_user_conflicts()
    {
        _users.Setup(u => u.ExistsAsync("newbie", default)).ReturnsAsync(true);

        var result = await Build().RequestAsync(Command(), Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("이미 사용 중인 ID");
        _requests.Verify(r => r.AddAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Request_pending_request_conflicts()
    {
        _requests.Setup(r => r.GetByUserIdAsync("newbie", default)).ReturnsAsync(PendingRequest());

        var result = await Build().RequestAsync(Command(), Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("이미 처리 대기 중이거나 승인된 신청");
        _requests.Verify(r => r.AddAsync(It.IsAny<UserRequest>(), default), Times.Never);
        _requests.Verify(r => r.UpdateAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Request_after_rejection_reapplies_same_row()
    {
        var rejected = RejectedRequest();
        _requests.Setup(r => r.GetByUserIdAsync("newbie", default)).ReturnsAsync(rejected);

        var result = await Build().RequestAsync(Command(), Now.AddDays(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(rejected, "재신청은 새 행이 아니라 같은 행을 Request로 전환한다");
        result.Value.RequestVersion.Should().Be(2);
        result.Value.Status.Should().Be(UserRequestStatus.Request);
        _requests.Verify(r => r.UpdateAsync(rejected, default), Times.Once);
        _requests.Verify(r => r.AddAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Request_invalid_input_fails_without_save()
    {
        var result = await Build().RequestAsync(Command(termsAccepted: false), Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UserRequest.TermsRequired");
        _requests.Verify(r => r.AddAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    // ── GetRequestAsync / GetRequestsAsync ────────────────────────────────────

    [Fact]
    public async Task GetRequest_unknown_fails_not_found()
    {
        _requests.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((UserRequest?)null);

        var result = await Build().GetRequestAsync("RXXX");

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task GetRequests_from_after_to_fails()
    {
        var result = await Build().GetRequestsAsync(from: Now.AddDays(1), to: Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UserRequest.InvalidPeriod");
        _requests.Verify(r => r.SearchAsync(
            It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), default), Times.Never);
    }

    [Fact]
    public async Task GetRequests_trims_filters_and_treats_blank_as_null()
    {
        _requests.Setup(r => r.SearchAsync(
                It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<string?>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), default))
            .ReturnsAsync(new List<UserRequest>());

        var result = await Build().GetRequestsAsync(" P1 ", "   ", " newbie ");

        result.IsSuccess.Should().BeTrue();
        _requests.Verify(r => r.SearchAsync(
            "P1", null, "newbie", null, null, null, null, default), Times.Once);
    }

    // ── ApproveAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve_creates_user_and_marks_request_approved()
    {
        var request = PendingRequest();
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(request);
        User? created = null;
        _requests.Setup(r => r.ApprovePersistAsync(It.IsAny<UserRequest>(), It.IsAny<User>(), default))
            .Callback<UserRequest, User, CancellationToken>((_, u, _) => created = u)
            .Returns(Task.CompletedTask);
        var hash = PasswordHasher.Hash("temp-password");

        var result = await Build().ApproveAsync("REQ1", "admin", "OPERATOR", hash, Now.AddHours(2));

        result.IsSuccess.Should().BeTrue();
        result.Value.Request.Status.Should().Be(UserRequestStatus.Approved);
        result.Value.Request.ApprovedBy.Should().Be("admin");
        created.Should().NotBeNull("승인 시점에만 SYS_USER 행이 생성된다 (§19.3.5)");
        created!.Id.Should().Be("newbie");
        created.RoleId.Should().Be("OPERATOR");
        created.Email.Should().Be("new@test.com");
        created.PasswordHash.Should().Be(hash);
        created.PasswordState.Should().Be(PasswordState.Create, "최초 로그인 시 비밀번호 변경을 강제한다");
        // DATA-6: 사용자 INSERT + 신청 UPDATE는 개별 호출이 아니라 단일 원자 배치로 영속된다.
        _requests.Verify(r => r.ApprovePersistAsync(request, created, default), Times.Once);
        _users.Verify(u => u.AddAsync(It.IsAny<User>(), default), Times.Never);
        _requests.Verify(r => r.UpdateAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Approve_blank_role_defaults_to_USER()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());
        User? created = null;
        _requests.Setup(r => r.ApprovePersistAsync(It.IsAny<UserRequest>(), It.IsAny<User>(), default))
            .Callback<UserRequest, User, CancellationToken>((_, u, _) => created = u)
            .Returns(Task.CompletedTask);

        var result = await Build().ApproveAsync("REQ1", "admin", "  ", PasswordHasher.Hash("t"), Now);

        result.IsSuccess.Should().BeTrue();
        created!.RoleId.Should().Be("USER", "역할 미지정 시 기본 역할 USER를 부여한다 (§19.3.5 적응)");
    }

    [Fact]
    public async Task Approve_unknown_request_fails_not_found()
    {
        _requests.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((UserRequest?)null);

        var result = await Build().ApproveAsync("RXXX", "admin", "USER", PasswordHasher.Hash("t"), Now);

        result.IsFailure.Should().BeTrue();
        _users.Verify(u => u.AddAsync(It.IsAny<User>(), default), Times.Never);
    }

    [Fact]
    public async Task Approve_already_processed_fails()
    {
        var request = PendingRequest();
        request.Approve("admin", Now);
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(request);

        var result = await Build().ApproveAsync("REQ1", "admin2", "USER", PasswordHasher.Hash("t"), Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UserRequest.InvalidState");
        _users.Verify(u => u.AddAsync(It.IsAny<User>(), default), Times.Never);
    }

    [Fact]
    public async Task Approve_existing_user_conflicts_without_creating()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());
        _users.Setup(u => u.ExistsAsync("newbie", default)).ReturnsAsync(true);

        var result = await Build().ApproveAsync("REQ1", "admin", "USER", PasswordHasher.Hash("t"), Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("이미 존재하는 사용자");
        _users.Verify(u => u.AddAsync(It.IsAny<User>(), default), Times.Never);
        _requests.Verify(r => r.UpdateAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    // ── RejectAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_marks_rejected_and_updates()
    {
        var request = PendingRequest();
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(request);

        var result = await Build().RejectAsync("REQ1", "admin", "서류 미비", Now.AddHours(2));

        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(UserRequestStatus.Rejected);
        result.Value.RejectReason.Should().Be("서류 미비");
        result.Value.RejectedBy.Should().Be("admin");
        _requests.Verify(r => r.UpdateAsync(request, default), Times.Once);
    }

    [Fact]
    public async Task Reject_without_reason_fails_without_save()
    {
        _requests.Setup(r => r.GetByIdAsync("REQ1", default)).ReturnsAsync(PendingRequest());

        var result = await Build().RejectAsync("REQ1", "admin", "   ", Now);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UserRequest.ReasonRequired");
        _requests.Verify(r => r.UpdateAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }

    [Fact]
    public async Task Reject_unknown_request_fails_not_found()
    {
        _requests.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((UserRequest?)null);

        var result = await Build().RejectAsync("RXXX", "admin", "사유", Now);

        result.IsFailure.Should().BeTrue();
        _requests.Verify(r => r.UpdateAsync(It.IsAny<UserRequest>(), default), Times.Never);
    }
}
