using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaOne.API.Extensions;
using NexaOne.API.Services;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.API.Controllers;

/// <summary>§19.3 — 사용자 등록 신청/승인. 신청·중복확인은 익명(로그인 전 화면),
/// 목록/승인/반려는 관리자 전용. 상태 전이는 이 API 서버를 통해서만 일어난다 (§19.3.7).</summary>
[ApiController]
[Route("api/v1/users")]
[Authorize(Policy = "perm:sys:user.manage")]   // ADR-003 — 역할 하드코딩 → 권한 정책(ADMIN=* 보유)
public class UserRegistrationController(
    UserApprovalService approvalService,
    UserRegistrationService registrationService) : ControllerBase
{
    private string CurrentUserId =>
        User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value ?? string.Empty;

    /// <summary>§19.3.2 — 아이디 중복 확인 (등록 화면의 [중복확인] 버튼).</summary>
    [HttpGet("exists/{userId}")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]   // §18.2.3 — 익명 진입점 IP당 제한 (ID 열거 방어)
    // 익명 객체 { available: bool } 반환 — 명명 타입이 없어 상태코드만 주석 (concerns 참조)
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> CheckUserId(string userId, CancellationToken ct)
        => Ok(new { available = await registrationService.IsUserIdAvailableAsync(userId, ct) });

    /// <summary>§19.3.3 — 등록 신청. 약관 동의 시각/IP를 서버에서 기록한다.</summary>
    [HttpPost("request")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]   // §18.2.3 — 익명 진입점 IP당 제한 (신청 폭주 방어)
    [ProducesResponseType<UserRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<Error>(StatusCodes.Status400BadRequest)]   // Validation·Failure
    [ProducesResponseType<Error>(StatusCodes.Status409Conflict)]     // 중복/처리중
    public async Task<IActionResult> SubmitRequest([FromBody] UserRegistrationRequest req, CancellationToken ct)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        // IsDefined — TryParse는 숫자 문자열("999")을 미정의 enum 값으로 통과시키므로 추가 검증
        var language = Enum.TryParse<LanguageType>(req.DefaultLanguageType, out var lang) && Enum.IsDefined(lang)
            ? lang : LanguageType.KoKr;

        var result = await approvalService.RequestAsync(new UserRegistrationCommand(
            req.UserId, req.UserName, req.Email, req.Department, req.Position,
            req.PlantId, language, req.TermsAccepted, ipAddress,
            req.Duty, req.CellPhoneNumber, req.Address, req.Description, req.Nickname), ct);

        return result.ToActionResult(r => Ok(ToDto(r)));
    }

    /// <summary>§19.3.4 — 승인 화면 목록 (Plant/상태/신청일/ID/이름/이메일 검색).</summary>
    [HttpGet("requests")]
    [ProducesResponseType<IEnumerable<UserRequestDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<Error>(StatusCodes.Status400BadRequest)]   // 잘못된 조회 기간
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRequests(
        [FromQuery] string? plantId, [FromQuery] string? status,
        [FromQuery] string? userId, [FromQuery] string? userName, [FromQuery] string? email,
        [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
    {
        var result = await registrationService.GetRequestsAsync(
            plantId, status, userId, userName, email, from, to, ct);
        return result.ToActionResult(requests => Ok(requests.Select(ToDto)));
    }

    [HttpGet("requests/{requestId}")]
    [ProducesResponseType<UserRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<Error>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRequest(string requestId, CancellationToken ct)
    {
        var result = await registrationService.GetRequestAsync(requestId, ct);
        return result.ToActionResult(r => Ok(ToDto(r)));
    }

    /// <summary>§19.3.5 — 승인. 사용자 생성(PasswordState=Create) + 임시 비밀번호 메일 발송.</summary>
    [HttpPatch("requests/{requestId}/approve")]
    [ProducesResponseType<UserRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<Error>(StatusCodes.Status400BadRequest)]   // 상태 불일치 등
    [ProducesResponseType<Error>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<Error>(StatusCodes.Status409Conflict)]     // 사용자 중복
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Approve(
        string requestId, [FromBody] ApproveUserRequestRequest req, CancellationToken ct)
    {
        var result = await approvalService.ApproveAsync(requestId, CurrentUserId, req.RoleId ?? "", ct);
        return result.ToActionResult(r => Ok(ToDto(r)));
    }

    /// <summary>§19.3.6 — 반려. 사유 필수, 반려 후 같은 ID로 재신청이 가능하다.</summary>
    [HttpPatch("requests/{requestId}/reject")]
    [ProducesResponseType<UserRequestDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<Error>(StatusCodes.Status400BadRequest)]   // 사유 누락 등
    [ProducesResponseType<Error>(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Reject(
        string requestId, [FromBody] RejectUserRequestRequest req, CancellationToken ct)
    {
        var result = await approvalService.RejectAsync(requestId, CurrentUserId, req.Reason ?? "", ct);
        return result.ToActionResult(r => Ok(ToDto(r)));
    }

    private static UserRequestDto ToDto(UserRequest r) => new(
        r.Id, r.UserId, r.UserName, r.Email, r.Department, r.Position, r.Duty,
        r.PlantId, r.Language.ToString(), r.CellPhoneNumber, r.Address, r.Description, r.Nickname,
        r.Status.ToString(), r.RequestVersion, r.RequestedAt, r.TermsAcceptedAt,
        r.ApprovedBy, r.ApprovedAt, r.RejectReason, r.RejectedBy, r.RejectedAt);
}

public record UserRegistrationRequest(
    string UserId, string UserName, string Email, string Department, string Position,
    string PlantId, string? DefaultLanguageType, bool TermsAccepted,
    string? Duty = null, string? CellPhoneNumber = null, string? Address = null,
    string? Description = null, string? Nickname = null);

public record ApproveUserRequestRequest(string? RoleId);
public record RejectUserRequestRequest(string? Reason);

public record UserRequestDto(
    string RequestId, string UserId, string UserName, string Email,
    string Department, string Position, string? Duty,
    string PlantId, string Language, string? CellPhoneNumber, string? Address,
    string? Description, string? Nickname,
    string Status, int RequestVersion, DateTime RequestedAt, DateTime TermsAcceptedAt,
    string? ApprovedBy, DateTime? ApprovedAt,
    string? RejectReason, string? RejectedBy, DateTime? RejectedAt);
