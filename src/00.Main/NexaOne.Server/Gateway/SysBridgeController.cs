using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 SYS 관리 엔드포인트(ADR-008 얇은 브리지). plugin-ALC UserService/UserRegistrationService를
/// ISysBridge로 호출한다. 쓰기(역할 관리·신청 생명주기·사용자 비활성)는 sys:manage — 단, 가입 신청/중복확인은
/// 익명 진입점(레이트리밋)이다(§19.3.2/3). Result→HTTP(BridgeResultExtensions). (modules ON에서만 동작.)
///
/// 경로는 api/v1/sys/admin — QueryCatalogController(api/v1/sys/queries)·AuthController(api/v1/auth)와 충돌하지 않는다.
/// 보안 가드(S7): 자격증명/로그인/리프레시·잠금 해제는 본 컨트롤러에 없다 — 인증은 격리 경로(AuthController +
/// GatewayLoginService + db/queries-auth)가 소유한다. 승인(§19.3.5)은 임시 비밀번호를 호스트가 생성·해싱해
/// "해시"만 브리지로 넘기고, 평문은 응답(1회 표시)과 메일에만 존재하며 절대 로그에 남기지 않는다.</summary>
[ApiController]
[Route("api/v1/sys/admin")]
[Authorize]
[ProducesErrorResponseType(typeof(Error))]
public sealed class SysBridgeController : ControllerBase
{
    private readonly ISysBridge _bridge;
    private readonly GatewayLoginService _login;
    private readonly IEmailSender _email;
    private readonly ILogger<SysBridgeController> _logger;

    public SysBridgeController(
        ISysBridge bridge, GatewayLoginService login, IEmailSender email, ILogger<SysBridgeController> logger)
    {
        _bridge = bridge;
        _login = login;
        _email = email;
        _logger = logger;
    }

    // ── 역할(Role) 관리 ──

    [HttpPost("roles")]
    [ProducesResponseType<RoleDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> CreateRole([FromBody] CreateRoleRequest req, CancellationToken ct)
    {
        return (await _bridge.CreateRoleAsync(req.RoleId, req.RoleName, req.Description, ct)).ToActionResult();
    }

    [HttpPost("roles/{roleId}/permissions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> AddPermission(string roleId, [FromBody] PermissionRequest req, CancellationToken ct)
    {
        return (await _bridge.AddPermissionAsync(roleId, req.Permission, ct)).ToActionResult();
    }

    [HttpDelete("roles/{roleId}/permissions")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> RemovePermission(string roleId, [FromBody] PermissionRequest req, CancellationToken ct)
    {
        return (await _bridge.RemovePermissionAsync(roleId, req.Permission, ct)).ToActionResult();
    }

    // ── 사용자 등록 신청 생명주기(§19.3) ──

    /// <summary>§19.3.2 — 아이디 중복 확인. 가입 화면의 익명 진입점 — 열거 남용은 auth 레이트리밋(IP당 10/min)으로 제동.</summary>
    [HttpGet("user-requests/availability")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<UserIdAvailabilityResponse>(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckUserIdAvailability([FromQuery] string userId, CancellationToken ct)
    {
        return Ok(new UserIdAvailabilityResponse(await _bridge.IsUserIdAvailableAsync(userId ?? string.Empty, ct)));
    }

    /// <summary>§19.3.3 — 등록 신청(익명). 비밀번호는 받지 않는다 — 승인 시 임시 비밀번호가 발급된다.
    /// 약관 동의 IP는 클라이언트 주장이 아닌 서버 연결 정보로 기록한다.</summary>
    [HttpPost("user-requests")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<UserRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateUserRequest([FromBody] CreateUserRequestRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var dto = new UserRegistrationRequestDto(
            req.UserId, req.UserName, req.Email, req.Department, req.Position,
            req.PlantId, req.Language ?? "KoKr", req.TermsAccepted, ip,
            req.Duty, req.CellPhoneNumber, req.Address, req.Description, req.Nickname);
        return (await _bridge.CreateRequestAsync(dto, ct)).ToActionResult();
    }

    /// <summary>§19.3.4 — 승인 화면 신청 목록 조회(검색 조건은 서비스가 trim/검증).</summary>
    [HttpGet("user-requests")]
    [ProducesResponseType<IReadOnlyList<UserRequestDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> GetUserRequests(
        [FromQuery] string? plantId, [FromQuery] string? status, [FromQuery] string? userId,
        [FromQuery] string? userName, [FromQuery] string? email,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        return (await _bridge.GetRequestsAsync(plantId, status, userId, userName, email, from, to, ct)).ToActionResult();
    }

    /// <summary>§19.3.5 — 승인: 활성 역할 검증(SEC-1과 동일 쿼리) 후 임시 비밀번호를 생성·해싱해 브리지로 넘긴다
    /// (SYS_USER 생성 + 신청 전환은 DATA-6 단일 트랜잭션). 평문 임시 비밀번호는 응답(관리자 1회 전달용)과
    /// 안내 메일에만 존재한다 — 최초 로그인 시 변경이 강제되므로(PasswordState=Create) 장기 노출이 없다.</summary>
    [HttpPost("user-requests/{requestId}/approve")]
    [ProducesResponseType<ApproveUserRequestResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> ApproveUserRequest(
        string requestId, [FromBody] ApproveUserRequestRequest req, CancellationToken ct)
    {
        var roleId = req.RoleId?.Trim() ?? string.Empty;
        if (roleId.Length == 0)
            return BadRequest(new Error("ROLE_REQUIRED", "승인 시 부여할 역할(roleId)은 필수입니다.", ErrorType.Validation));
        if (!await _login.RoleExistsAsync(roleId, ct))
            return BadRequest(new Error("INVALID_ROLE", $"Role '{roleId}' does not exist or is inactive.", ErrorType.Validation));

        var approvedBy = User.CurrentUserId() ?? "SYSTEM";
        var tempPassword = GenerateTempPassword();
        var result = await _bridge.ApproveRequestAsync(requestId, roleId, approvedBy, PasswordHasher.Hash(tempPassword), ct);
        if (result.IsFailure)
            return result.ToActionResult();

        try
        {
            await _email.SendAsync(result.Value.Email, "[SmartEES] 계정 승인 안내",
                $"'{result.Value.UserId}' 계정이 승인되었습니다.\n임시 비밀번호: {tempPassword}\n최초 로그인 시 비밀번호를 변경해야 합니다.", ct);
        }
        catch (Exception ex)
        {
            // 메일 실패가 승인(이미 커밋됨)을 되돌리지 않는다 — 임시 비밀번호는 응답으로 관리자에게 전달된다.
            _logger.LogWarning(ex, "승인 안내 메일 발송 실패 — userId={UserId}", result.Value.UserId);
        }
        return Ok(new ApproveUserRequestResponse(result.Value, tempPassword));
    }

    // ── 사용자 등록 신청 반려 ──

    [HttpPost("user-requests/{requestId}/reject")]
    [ProducesResponseType<UserRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> RejectRequest(string requestId, [FromBody] RejectRequestRequest req, CancellationToken ct)
    {
        var rejectedBy = User.CurrentUserId() ?? "SYSTEM";
        return (await _bridge.RejectRequestAsync(requestId, rejectedBy, req.Reason, ct)).ToActionResult();
    }

    // ── 사용자 비활성(상태전이) ──

    [HttpPost("users/{userId}/deactivate")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [RequirePermission(Permissions.SysManage)]
    public async Task<IActionResult> DeactivateUser(string userId, CancellationToken ct)
    {
        return (await _bridge.DeactivateUserAsync(userId, ct)).ToActionResult();
    }

    /// <summary>임시 비밀번호 생성 — §19.2.2 정책(12자, 대/소문자·숫자·특수문자 각 1+ 보장)을 crypto RNG로 충족.</summary>
    private static string GenerateTempPassword()
    {
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";   // 혼동 문자(I/O) 제외
        const string lower = "abcdefghijkmnpqrstuvwxyz";   // 혼동 문자(l/o) 제외
        const string digits = "23456789";
        const string special = "!@#$%^*+-=";
        const string all = upper + lower + digits + special;

        var chars = new char[12];
        chars[0] = upper[RandomNumberGenerator.GetInt32(upper.Length)];
        chars[1] = lower[RandomNumberGenerator.GetInt32(lower.Length)];
        chars[2] = digits[RandomNumberGenerator.GetInt32(digits.Length)];
        chars[3] = special[RandomNumberGenerator.GetInt32(special.Length)];
        for (var i = 4; i < chars.Length; i++)
            chars[i] = all[RandomNumberGenerator.GetInt32(all.Length)];

        // Fisher–Yates 셔플(crypto RNG) — 클래스 보장 문자가 항상 앞자리에 오는 패턴 제거
        for (var i = chars.Length - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars);
    }
}

public record CreateRoleRequest(string RoleId, string RoleName, string Description);
public record PermissionRequest(string Permission);
public record RejectRequestRequest(string Reason);
public record UserIdAvailabilityResponse(bool Available);
public record CreateUserRequestRequest(
    string UserId, string UserName, string Email, string Department, string Position, string PlantId,
    bool TermsAccepted, string? Language = null, string? Duty = null,
    string? CellPhoneNumber = null, string? Address = null, string? Description = null, string? Nickname = null);
public record ApproveUserRequestRequest(string? RoleId);
public record ApproveUserRequestResponse(UserRequestDto Request, string TempPassword);
