using NexaOne.Common;
using NexaOne.SYS.Domain;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.SYS.Application.Users;

/// <summary>ADR-008 얇은 브리지 어댑터 — UserService(역할 관리·사용자 비활성)·UserRegistrationService(신청 생명주기)에
/// 위임하고 도메인 엔티티를 계약 DTO로 매핑(Status enum→string). plugin ALC에서 생성되며 호스트(Default ALC)가
/// ISysBridge로 캐스트해 DI에 등록한다. 팩토리/상태전이 Result는 그대로 통과시켜 컨트롤러가 409/400/404로 매핑한다.
///
/// 보안 가드(S7): 자격증명/비밀번호/로그인/리프레시·잠금 해제는 본 어댑터가 위임하지 않는다(인증 경로 소유).
/// 승인은 DATA-6 원자화(ApprovePersistAsync)로 노출하되, 호스트가 해싱한 임시 비밀번호 "해시"만 통과시킨다.</summary>
public sealed class SysBridge : ISysBridge
{
    private readonly UserService _userService;
    private readonly UserRegistrationService _registrationService;

    public SysBridge(UserService userService, UserRegistrationService registrationService)
    {
        _userService = userService;
        _registrationService = registrationService;
    }

    // ── 역할 관리 ──

    public async Task<Result<RoleDto>> CreateRoleAsync(
        string roleId, string roleName, string description, CancellationToken ct = default)
    {
        var r = await _userService.CreateRoleAsync(roleId, roleName, description, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<RoleDto>(r.Error);
    }

    public Task<Result> AddPermissionAsync(string roleId, string permission, CancellationToken ct = default)
        => _userService.AddPermissionAsync(roleId, permission, ct);

    public Task<Result> RemovePermissionAsync(string roleId, string permission, CancellationToken ct = default)
        => _userService.RemovePermissionAsync(roleId, permission, ct);

    // ── 사용자 등록 신청 생명주기(§19.3) ──

    public Task<bool> IsUserIdAvailableAsync(string userId, CancellationToken ct = default)
        => _registrationService.IsUserIdAvailableAsync(userId, ct);

    public async Task<Result<UserRequestDto>> CreateRequestAsync(
        UserRegistrationRequestDto request, CancellationToken ct = default)
    {
        var command = new UserRegistrationCommand(
            request.UserId, request.UserName, request.Email, request.Department, request.Position,
            request.PlantId, ParseLanguage(request.Language), request.TermsAccepted, request.TermsAcceptedIp,
            request.Duty, request.CellPhoneNumber, request.Address, request.Description, request.Nickname);
        var r = await _registrationService.RequestAsync(command, DateTime.UtcNow, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<UserRequestDto>(r.Error);
    }

    public async Task<Result<IReadOnlyList<UserRequestDto>>> GetRequestsAsync(
        string? plantId = null, string? status = null, string? userId = null,
        string? userName = null, string? email = null,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var r = await _registrationService.GetRequestsAsync(plantId, status, userId, userName, email, from, to, ct);
        return r.IsSuccess
            ? Result.Success<IReadOnlyList<UserRequestDto>>(r.Value.Select(ToDto).ToList())
            : Result.Failure<IReadOnlyList<UserRequestDto>>(r.Error);
    }

    public async Task<Result<UserRequestDto>> ApproveRequestAsync(
        string requestId, string roleId, string approvedBy, string tempPasswordHash, CancellationToken ct = default)
    {
        var r = await _registrationService.ApproveAsync(requestId, approvedBy, roleId, tempPasswordHash, DateTime.UtcNow, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value.Request)) : Result.Failure<UserRequestDto>(r.Error);
    }

    public async Task<Result<UserRequestDto>> RejectRequestAsync(
        string requestId, string rejectedBy, string reason, CancellationToken ct = default)
    {
        var r = await _registrationService.RejectAsync(requestId, rejectedBy, reason, DateTime.UtcNow, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<UserRequestDto>(r.Error);
    }

    // ── 사용자 비활성 ──

    public Task<Result> DeactivateUserAsync(string userId, CancellationToken ct = default)
        => _userService.DeactivateUserAsync(userId, ct);

    // ── 매핑 ──

    private static LanguageType ParseLanguage(string? language)
        => Enum.TryParse<LanguageType>(language?.Replace("-", ""), ignoreCase: true, out var parsed)
            ? parsed
            : LanguageType.KoKr;   // "ko-KR"/"KoKr" 모두 수용, 미지원 값은 기본 언어로 저하

    private static RoleDto ToDto(Role r)
        => new(r.Id, r.RoleName, r.Description, r.Permissions);

    private static UserRequestDto ToDto(UserRequest u)
        => new(u.Id, u.UserId, u.UserName, u.Email, u.Department, u.Position,
            u.Duty, u.PlantId, u.Language.ToString(), u.CellPhoneNumber, u.Address,
            u.Description, u.Nickname, u.Status.ToString(), u.RequestVersion,
            u.RequestedAt, u.TermsAcceptedAt,
            u.ApprovedBy, u.ApprovedAt, u.RejectReason, u.RejectedBy, u.RejectedAt);
}
