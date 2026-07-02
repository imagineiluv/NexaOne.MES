using NexaOne.Common;
using NexaOne.SYS.Domain;

namespace NexaOne.SYS.Application.Users;

/// <summary>§19.3.3 — 등록 신청 입력. termsAccepted=false인 신청은 거부된다.</summary>
public sealed record UserRegistrationCommand(
    string UserId,
    string UserName,
    string Email,
    string Department,
    string Position,
    string PlantId,
    LanguageType Language,
    bool TermsAccepted,
    string TermsAcceptedIp,
    string? Duty = null,
    string? CellPhoneNumber = null,
    string? Address = null,
    string? Description = null,
    string? Nickname = null);

/// <summary>
/// §19.3 — 사용자 등록 신청/승인 흐름. 신청 생명주기는 SYS_USER_REQUEST가 단독 소유하고,
/// SYS_USER 행은 승인 시점에만 생성한다. 메일 발송/감사 로그는 API 계층(UserApprovalService)이 담당한다.
/// </summary>
public sealed class UserRegistrationService
{
    private readonly IUserRequestRepository _requests;
    private readonly IUserRepository _users;

    public UserRegistrationService(IUserRequestRepository requests, IUserRepository users)
    {
        _requests = requests;
        _users = users;
    }

    /// <summary>§19.3.2 — 아이디 중복 확인. 기존 사용자 또는 미처리(대기/승인) 신청이 있으면 사용 불가.</summary>
    public async Task<bool> IsUserIdAvailableAsync(string userId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        if (await _users.ExistsAsync(userId.Trim(), ct)) return false;

        var existing = await _requests.GetByUserIdAsync(userId.Trim(), ct);
        // 반려된 신청은 같은 ID로 재신청이 가능하므로 사용 가능으로 취급한다 (§19.3.6)
        return existing is null || existing.Status == UserRequestStatus.Rejected;
    }

    /// <summary>§19.3.3 — 등록 신청 처리. 반려 이력이 있으면 같은 행을 재신청으로 전환한다(version 증가).</summary>
    public async Task<Result<UserRequest>> RequestAsync(
        UserRegistrationCommand command, DateTime utcNow, CancellationToken ct = default)
    {
        var userId = command.UserId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(userId))
            return Result.Failure<UserRequest>(
                Error.Validation("UserRequest.UserIdRequired", "사용자 ID는 필수입니다."));

        // ① 중복 확인 — 이미 존재하는 사용자/대기 중 신청은 거부 (§19.3.3)
        if (await _users.ExistsAsync(userId, ct))
            return Result.Failure<UserRequest>(
                Error.Conflict($"이미 사용 중인 ID입니다: {userId}"));

        var existing = await _requests.GetByUserIdAsync(userId, ct);
        if (existing is not null)
        {
            if (existing.Status != UserRequestStatus.Rejected)
                return Result.Failure<UserRequest>(
                    Error.Conflict($"이미 처리 대기 중이거나 승인된 신청이 있습니다: {userId}"));

            // §19.3.6 — 반려 후 재신청: 같은 행을 Request로 전환 + REQUEST_VERSION 증가
            var reapply = existing.Reapply(
                command.UserName, command.Email, command.Department, command.Position,
                command.PlantId, command.Language, command.TermsAccepted, utcNow,
                command.TermsAcceptedIp, command.Duty, command.CellPhoneNumber,
                command.Address, command.Description, command.Nickname);
            if (reapply.IsFailure)
                return Result.Failure<UserRequest>(reapply.Error);

            await _requests.UpdateAsync(existing, ct);
            return Result.Success(existing);
        }

        var created = UserRequest.Create(
            Guid.NewGuid().ToString("N"), userId,
            command.UserName, command.Email, command.Department, command.Position,
            command.PlantId, command.Language, command.TermsAccepted, utcNow,
            command.TermsAcceptedIp, command.Duty, command.CellPhoneNumber,
            command.Address, command.Description, command.Nickname);
        if (created.IsFailure)
            return created;

        await _requests.AddAsync(created.Value, ct);
        return created;
    }

    public async Task<Result<UserRequest>> GetRequestAsync(string requestId, CancellationToken ct = default)
    {
        var request = await _requests.GetByIdAsync(requestId, ct);
        return request is null
            ? Result.Failure<UserRequest>(Error.NotFound(nameof(UserRequest), requestId))
            : Result.Success(request);
    }

    /// <summary>§19.3.4 — 승인 화면 목록 조회. 검색어는 trim하고 공백은 전체로 취급한다.</summary>
    public async Task<Result<IReadOnlyList<UserRequest>>> GetRequestsAsync(
        string? plantId = null, string? status = null, string? userId = null,
        string? userName = null, string? email = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        if (from.HasValue && to.HasValue && from > to)
            return Result.Failure<IReadOnlyList<UserRequest>>(
                Error.Validation("UserRequest.InvalidPeriod", "조회 시작일이 종료일보다 늦습니다."));

        var requests = await _requests.SearchAsync(
            Clean(plantId), Clean(status), Clean(userId), Clean(userName), Clean(email),
            from, to, ct);
        return Result.Success(requests);
    }

    /// <summary>§19.3.5 — 승인: SYS_USER 행 생성(PasswordState=Create, 임시 비밀번호 해시) + 신청 Approved 전환을
    /// 단일 트랜잭션으로 영속한다(DATA-6 원자화 — 사용자만 생성되고 신청이 대기로 남는 부분 커밋 불가).</summary>
    public async Task<Result<(UserRequest Request, User User)>> ApproveAsync(
        string requestId, string approvedBy, string roleId, string tempPasswordHash,
        DateTime utcNow, CancellationToken ct = default)
    {
        var request = await _requests.GetByIdAsync(requestId, ct);
        if (request is null)
            return Result.Failure<(UserRequest, User)>(Error.NotFound(nameof(UserRequest), requestId));

        if (request.Status != UserRequestStatus.Request)
            return Result.Failure<(UserRequest, User)>(Error.Validation(
                "UserRequest.InvalidState", $"처리 대기 상태가 아닙니다 (현재: {request.Status})."));

        if (await _users.ExistsAsync(request.UserId, ct))
            return Result.Failure<(UserRequest, User)>(
                Error.Conflict($"이미 존재하는 사용자입니다: {request.UserId}"));

        var effectiveRole = string.IsNullOrWhiteSpace(roleId) ? "USER" : roleId.Trim();
        var userResult = User.Create(
            request.UserId, request.UserName, tempPasswordHash,
            request.Email, effectiveRole, request.Language);
        if (userResult.IsFailure)
            return Result.Failure<(UserRequest, User)>(userResult.Error);

        var user = userResult.Value;
        user.IssueInitialPassword(tempPasswordHash);   // PasswordState=Create — 최초 로그인 시 변경 강제

        var approve = request.Approve(approvedBy, utcNow);
        if (approve.IsFailure)
            return Result.Failure<(UserRequest, User)>(approve.Error);

        // 단일 트랜잭션 영속(DATA-6) — 검증/전이 실패는 이 지점 이전에 반환되므로 부분 커밋이 불가능하다.
        await _requests.ApprovePersistAsync(request, user, ct);
        return Result.Success((request, user));
    }

    /// <summary>§19.3.6 — 반려: 사유 필수, Rejected로 전환. 같은 ID로 재신청이 가능해진다.</summary>
    public async Task<Result<UserRequest>> RejectAsync(
        string requestId, string rejectedBy, string reason, DateTime utcNow, CancellationToken ct = default)
    {
        var request = await _requests.GetByIdAsync(requestId, ct);
        if (request is null)
            return Result.Failure<UserRequest>(Error.NotFound(nameof(UserRequest), requestId));

        var reject = request.Reject(rejectedBy, reason, utcNow);
        if (reject.IsFailure)
            return Result.Failure<UserRequest>(reject.Error);

        await _requests.UpdateAsync(request, ct);
        return Result.Success(request);
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
