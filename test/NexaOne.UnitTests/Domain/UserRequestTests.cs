using NexaOne.Common;
using NexaOne.SYS.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>§19.3 — UserRequest 상태 전이(Request→Approved/Rejected→Request)와
/// 필수 입력·약관 동의 검증, 재신청 시 버전 증가와 반려 이력 보존을 검증한다.</summary>
public sealed class UserRequestTests
{
    private static readonly DateTime Now = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    private static UserRequest NewRequest(string userId = "newbie") =>
        UserRequest.Create("REQ1", userId, "홍길동", "new@test.com", "생산팀", "사원",
            "P1", LanguageType.KoKr, termsAccepted: true, Now, "10.0.0.1").Value;

    private static UserRequest RejectedRequest()
    {
        var request = NewRequest();
        request.Reject("admin", "부서 확인 불가", Now.AddHours(1));
        return request;
    }

    // ── Create ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_sets_initial_state_and_timestamps()
    {
        var request = NewRequest();

        request.Status.Should().Be(UserRequestStatus.Request);
        request.RequestVersion.Should().Be(1);
        request.RequestedAt.Should().Be(Now);
        request.TermsAcceptedAt.Should().Be(Now, "약관 동의는 신청 제출과 동시에 이뤄진다 (§19.3.2)");
        request.TermsAcceptedIp.Should().Be("10.0.0.1");
        request.ApprovedBy.Should().BeNull();
        request.RejectReason.Should().BeNull();
    }

    [Fact]
    public void Create_trims_fields_and_normalizes_blank_optionals_to_null()
    {
        var result = UserRequest.Create("REQ1", " newbie ", " 홍길동 ", " new@test.com ",
            " 생산팀 ", " 사원 ", " P1 ", LanguageType.KoKr, termsAccepted: true, Now, "10.0.0.1",
            duty: "   ", cellPhoneNumber: " 010-1234-5678 ");

        var request = result.Value;
        request.UserId.Should().Be("newbie");
        request.UserName.Should().Be("홍길동");
        request.Email.Should().Be("new@test.com");
        request.Department.Should().Be("생산팀");
        request.Position.Should().Be("사원");
        request.PlantId.Should().Be("P1");
        request.Duty.Should().BeNull("공백 선택 항목은 null로 정규화된다");
        request.CellPhoneNumber.Should().Be("010-1234-5678");
    }

    [Theory]
    [InlineData("",       "홍길동", "a@b.c",   "생산팀", "사원", "P1", "UserRequest.UserIdRequired")]
    [InlineData("newbie", "  ",     "a@b.c",   "생산팀", "사원", "P1", "UserRequest.UserNameRequired")]
    [InlineData("newbie", "홍길동", "",         "생산팀", "사원", "P1", "UserRequest.EmailInvalid")]
    [InlineData("newbie", "홍길동", "no-at",    "생산팀", "사원", "P1", "UserRequest.EmailInvalid")]
    [InlineData("newbie", "홍길동", "a@b.c",   "",       "사원", "P1", "UserRequest.DepartmentRequired")]
    [InlineData("newbie", "홍길동", "a@b.c",   "생산팀", "  ",   "P1", "UserRequest.PositionRequired")]
    [InlineData("newbie", "홍길동", "a@b.c",   "생산팀", "사원", "",   "UserRequest.PlantRequired")]
    public void Create_missing_required_field_fails(
        string userId, string userName, string email,
        string department, string position, string plantId, string expectedCode)
    {
        var result = UserRequest.Create("REQ1", userId, userName, email, department, position,
            plantId, LanguageType.KoKr, termsAccepted: true, Now, "10.0.0.1");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be(expectedCode);
    }

    [Fact]
    public void Create_without_terms_fails()
    {
        var result = UserRequest.Create("REQ1", "newbie", "홍길동", "new@test.com", "생산팀", "사원",
            "P1", LanguageType.KoKr, termsAccepted: false, Now, "10.0.0.1");

        result.IsFailure.Should().BeTrue("약관 동의 없는 신청은 생성 자체가 불가하다 (§19.3.2)");
        result.Error.Code.Should().Be("UserRequest.TermsRequired");
    }

    // ── Approve ───────────────────────────────────────────────────────────────

    [Fact]
    public void Approve_marks_approved_with_approver_and_time()
    {
        var request = NewRequest();
        var at = Now.AddHours(2);

        var result = request.Approve("admin", at);

        result.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(UserRequestStatus.Approved);
        request.ApprovedBy.Should().Be("admin");
        request.ApprovedAt.Should().Be(at);
    }

    [Fact]
    public void Approve_non_pending_fails()
    {
        var request = NewRequest();
        request.Approve("admin", Now);

        var result = request.Approve("admin2", Now.AddHours(1));

        result.IsFailure.Should().BeTrue("승인은 Request 상태에서만 가능하다 (§19.3.7)");
        result.Error.Code.Should().Be("UserRequest.InvalidState");
        request.ApprovedBy.Should().Be("admin", "이미 처리된 승인 기록을 덮어쓰면 안 된다");
    }

    // ── Reject ────────────────────────────────────────────────────────────────

    [Fact]
    public void Reject_records_reason_rejecter_and_time()
    {
        var request = NewRequest();
        var at = Now.AddHours(2);

        var result = request.Reject("admin", " 부서 확인 불가 ", at);

        result.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(UserRequestStatus.Rejected);
        request.RejectedBy.Should().Be("admin");
        request.RejectReason.Should().Be("부서 확인 불가", "반려 사유는 trim해서 저장한다");
        request.RejectedAt.Should().Be(at);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Reject_requires_reason(string reason)
    {
        var request = NewRequest();

        var result = request.Reject("admin", reason, Now);

        result.IsFailure.Should().BeTrue("반려 사유는 필수다 (§19.3.6)");
        result.Error.Code.Should().Be("UserRequest.ReasonRequired");
        request.Status.Should().Be(UserRequestStatus.Request);
    }

    [Fact]
    public void Reject_non_pending_fails()
    {
        var request = NewRequest();
        request.Approve("admin", Now);

        var result = request.Reject("admin", "사유", Now.AddHours(1));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UserRequest.InvalidState");
    }

    // ── Reapply ───────────────────────────────────────────────────────────────

    [Fact]
    public void Reapply_returns_to_request_and_increments_version()
    {
        var request = RejectedRequest();
        var at = Now.AddDays(1);

        var result = request.Reapply("홍길순", "second@test.com", "품질팀", "대리",
            "P2", LanguageType.EnUs, termsAccepted: true, at, "10.0.0.2");

        result.IsSuccess.Should().BeTrue();
        request.Status.Should().Be(UserRequestStatus.Request);
        request.RequestVersion.Should().Be(2, "재신청마다 REQUEST_VERSION이 1씩 증가한다 (§19.3.6)");
        request.UserName.Should().Be("홍길순");
        request.Email.Should().Be("second@test.com");
        request.Department.Should().Be("품질팀");
        request.Position.Should().Be("대리");
        request.PlantId.Should().Be("P2");
        request.Language.Should().Be(LanguageType.EnUs);
        request.RequestedAt.Should().Be(at, "재신청 시 신청일이 갱신된다");
        request.TermsAcceptedAt.Should().Be(at);
        request.TermsAcceptedIp.Should().Be("10.0.0.2");
    }

    [Fact]
    public void Reapply_preserves_previous_rejection_history()
    {
        var request = RejectedRequest();

        request.Reapply("홍길동", "new@test.com", "생산팀", "사원",
            "P1", LanguageType.KoKr, termsAccepted: true, Now.AddDays(1), "10.0.0.2");

        request.RejectReason.Should().Be("부서 확인 불가", "이전 반려 이력은 보존한다 (§19.3.6)");
        request.RejectedBy.Should().Be("admin");
        request.RejectedAt.Should().NotBeNull();
    }

    [Fact]
    public void Reapply_only_allowed_from_rejected()
    {
        var request = NewRequest();

        var result = request.Reapply("홍길동", "new@test.com", "생산팀", "사원",
            "P1", LanguageType.KoKr, termsAccepted: true, Now.AddDays(1), "10.0.0.2");

        result.IsFailure.Should().BeTrue("재신청은 반려 상태에서만 가능하다 (§19.3.7)");
        result.Error.Code.Should().Be("UserRequest.InvalidState");
        request.RequestVersion.Should().Be(1);
    }

    [Fact]
    public void Reapply_without_terms_fails_and_keeps_rejected_state()
    {
        var request = RejectedRequest();

        var result = request.Reapply("홍길동", "new@test.com", "생산팀", "사원",
            "P1", LanguageType.KoKr, termsAccepted: false, Now.AddDays(1), "10.0.0.2");

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("UserRequest.TermsRequired");
        request.Status.Should().Be(UserRequestStatus.Rejected, "검증 실패 시 상태가 변하면 안 된다");
        request.RequestVersion.Should().Be(1);
    }
}
