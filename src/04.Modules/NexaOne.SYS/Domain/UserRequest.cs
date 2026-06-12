using NexaOne.Common;

namespace NexaOne.SYS.Domain;

/// <summary>§19.3.7 — 신청 상태. Request→Approved(승인)/Rejected(반려), Rejected→Request(재신청).</summary>
public enum UserRequestStatus
{
    Request,
    Approved,
    Rejected
}

/// <summary>
/// §19.3 — 사용자 등록 신청. 설계 표준의 SYS_TB_USER.STATE/VALID_STATE 대신
/// 신청 생명주기를 이 엔티티가 단독 소유하고, SYS_USER 행은 승인 시점에만 생성한다.
/// </summary>
public sealed class UserRequest : AuditableEntity<string>
{
    private UserRequest(string requestId) : base(requestId) { }

    public string UserId { get; private set; } = string.Empty;
    public string UserName { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string Department { get; private set; } = string.Empty;
    /// <summary>직급 (필수, 설계 19.3.2).</summary>
    public string Position { get; private set; } = string.Empty;
    /// <summary>직책 (선택, 설계 19.3.2).</summary>
    public string? Duty { get; private set; }
    public string PlantId { get; private set; } = string.Empty;
    public LanguageType Language { get; private set; }
    public string? CellPhoneNumber { get; private set; }
    public string? Address { get; private set; }
    public string? Description { get; private set; }
    public string? Nickname { get; private set; }

    public UserRequestStatus Status { get; private set; } = UserRequestStatus.Request;

    /// <summary>§19.3.6 — 재신청 횟수 추적. 반려 후 재신청 시 1씩 증가한다.</summary>
    public int RequestVersion { get; private set; } = 1;

    /// <summary>신청 시각(UTC) — 재신청 시 갱신된다. 승인 화면의 '신청일' 검색 기준.</summary>
    public DateTime RequestedAt { get; private set; }

    /// <summary>§19.3.2 — 약관 동의 시각(UTC). 동의 없는 신청은 생성 자체가 불가하다.</summary>
    public DateTime TermsAcceptedAt { get; private set; }
    public string TermsAcceptedIp { get; private set; } = string.Empty;

    public string? ApprovedBy { get; private set; }
    public DateTime? ApprovedAt { get; private set; }
    public string? RejectReason { get; private set; }
    public string? RejectedBy { get; private set; }
    public DateTime? RejectedAt { get; private set; }

    public static Result<UserRequest> Create(
        string requestId,
        string userId,
        string userName,
        string email,
        string department,
        string position,
        string plantId,
        LanguageType language,
        bool termsAccepted,
        DateTime utcNow,
        string termsAcceptedIp,
        string? duty = null,
        string? cellPhoneNumber = null,
        string? address = null,
        string? description = null,
        string? nickname = null)
    {
        var violation = ValidateRequired(userId, userName, email, department, position, plantId, termsAccepted);
        if (violation is not null)
            return Result.Failure<UserRequest>(violation);

        return new UserRequest(requestId)
        {
            UserId = userId.Trim(),
            UserName = userName.Trim(),
            Email = email.Trim(),
            Department = department.Trim(),
            Position = position.Trim(),
            PlantId = plantId.Trim(),
            Language = language,
            RequestedAt = utcNow,
            TermsAcceptedAt = utcNow,   // 약관 동의는 신청 제출과 동시에 이뤄진다 (§19.3.2)
            TermsAcceptedIp = termsAcceptedIp,
            Duty = Normalize(duty),
            CellPhoneNumber = Normalize(cellPhoneNumber),
            Address = Normalize(address),
            Description = Normalize(description),
            Nickname = Normalize(nickname)
        };
    }

    /// <summary>DB 행 복원 전용 — 저장된 상태를 검증 없이 그대로 되살린다. 신규 생성은 Create를 사용한다.</summary>
    public static UserRequest Restore(
        string requestId,
        string userId,
        string userName,
        string email,
        string department,
        string position,
        string? duty,
        string plantId,
        LanguageType language,
        string? cellPhoneNumber,
        string? address,
        string? description,
        string? nickname,
        UserRequestStatus status,
        int requestVersion,
        DateTime requestedAt,
        DateTime termsAcceptedAt,
        string termsAcceptedIp,
        string? approvedBy,
        DateTime? approvedAt,
        string? rejectReason,
        string? rejectedBy,
        DateTime? rejectedAt)
        => new(requestId)
        {
            UserId = userId,
            UserName = userName,
            Email = email,
            Department = department,
            Position = position,
            Duty = duty,
            PlantId = plantId,
            Language = language,
            CellPhoneNumber = cellPhoneNumber,
            Address = address,
            Description = description,
            Nickname = nickname,
            Status = status,
            RequestVersion = requestVersion,
            RequestedAt = requestedAt,
            TermsAcceptedAt = termsAcceptedAt,
            TermsAcceptedIp = termsAcceptedIp,
            ApprovedBy = approvedBy,
            ApprovedAt = approvedAt,
            RejectReason = rejectReason,
            RejectedBy = rejectedBy,
            RejectedAt = rejectedAt
        };

    /// <summary>§19.3.5 — 승인. Request 상태에서만 가능하며, 승인자/시각을 기록한다.</summary>
    public Result Approve(string approvedBy, DateTime utcNow)
    {
        if (Status != UserRequestStatus.Request)
            return Result.Failure(Error.Validation(
                "UserRequest.InvalidState", $"처리 대기 상태가 아닙니다 (현재: {Status})."));

        Status = UserRequestStatus.Approved;
        ApprovedBy = approvedBy;
        ApprovedAt = utcNow;
        return Result.Success();
    }

    /// <summary>§19.3.6 — 반려. 사유는 필수이며, 반려자/시각/사유를 기록한다.</summary>
    public Result Reject(string rejectedBy, string reason, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return Result.Failure(Error.Validation(
                "UserRequest.ReasonRequired", "반려 사유는 필수입니다."));
        if (Status != UserRequestStatus.Request)
            return Result.Failure(Error.Validation(
                "UserRequest.InvalidState", $"처리 대기 상태가 아닙니다 (현재: {Status})."));

        Status = UserRequestStatus.Rejected;
        RejectedBy = rejectedBy;
        RejectReason = reason.Trim();
        RejectedAt = utcNow;
        return Result.Success();
    }

    /// <summary>§19.3.6/§19.3.7 — 반려 후 재신청. 같은 행을 Request로 전환하고 버전을 올린다.
    /// 입력 항목은 재신청 내용으로 갱신하며, 이전 반려 이력(사유/반려자)은 보존한다.</summary>
    public Result Reapply(
        string userName,
        string email,
        string department,
        string position,
        string plantId,
        LanguageType language,
        bool termsAccepted,
        DateTime utcNow,
        string termsAcceptedIp,
        string? duty = null,
        string? cellPhoneNumber = null,
        string? address = null,
        string? description = null,
        string? nickname = null)
    {
        if (Status != UserRequestStatus.Rejected)
            return Result.Failure(Error.Validation(
                "UserRequest.InvalidState", $"반려 상태에서만 재신청할 수 있습니다 (현재: {Status})."));

        var violation = ValidateRequired(UserId, userName, email, department, position, plantId, termsAccepted);
        if (violation is not null)
            return Result.Failure(violation);

        UserName = userName.Trim();
        Email = email.Trim();
        Department = department.Trim();
        Position = position.Trim();
        PlantId = plantId.Trim();
        Language = language;
        RequestedAt = utcNow;
        TermsAcceptedAt = utcNow;
        TermsAcceptedIp = termsAcceptedIp;
        Duty = Normalize(duty);
        CellPhoneNumber = Normalize(cellPhoneNumber);
        Address = Normalize(address);
        Description = Normalize(description);
        Nickname = Normalize(nickname);

        Status = UserRequestStatus.Request;
        RequestVersion++;
        return Result.Success();
    }

    private static Error? ValidateRequired(
        string userId, string userName, string email,
        string department, string position, string plantId, bool termsAccepted)
    {
        if (string.IsNullOrWhiteSpace(userId))
            return Error.Validation("UserRequest.UserIdRequired", "사용자 ID는 필수입니다.");
        if (string.IsNullOrWhiteSpace(userName))
            return Error.Validation("UserRequest.UserNameRequired", "이름은 필수입니다.");
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return Error.Validation("UserRequest.EmailInvalid", "올바른 이메일 주소가 필요합니다.");
        if (string.IsNullOrWhiteSpace(department))
            return Error.Validation("UserRequest.DepartmentRequired", "부서는 필수입니다.");
        if (string.IsNullOrWhiteSpace(position))
            return Error.Validation("UserRequest.PositionRequired", "직급은 필수입니다.");
        if (string.IsNullOrWhiteSpace(plantId))
            return Error.Validation("UserRequest.PlantRequired", "플랜트는 필수입니다.");
        if (!termsAccepted)
            return Error.Validation("UserRequest.TermsRequired", "약관 동의는 필수입니다.");
        return null;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
