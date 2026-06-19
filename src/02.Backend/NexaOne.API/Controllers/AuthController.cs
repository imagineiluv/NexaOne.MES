using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaOne.API.Controllers.Models;
using NexaOne.API.Services;
using NexaOne.Common;
using NexaOne.SYS.Application.Users;

namespace NexaOne.API.Controllers;

[ApiController]
[Route("api/v1/auth")]
public class AuthController : ControllerBase
{
    private readonly IJwtService _jwtService;
    private readonly IRefreshTokenStore _tokenStore;
    private readonly UserService _userService;
    private readonly PasswordResetService _passwordResetService;

    public AuthController(
        IJwtService jwtService,
        IRefreshTokenStore tokenStore,
        UserService userService,
        PasswordResetService passwordResetService)
    {
        _jwtService = jwtService;
        _tokenStore = tokenStore;
        _userService = userService;
        _passwordResetService = passwordResetService;
    }

    [HttpPost("login")]
    [AllowAnonymous]               // 전역 FallbackPolicy(인증 요구) 예외 — 익명 진입점
    [EnableRateLimiting("auth")]   // §18.2.3 — 익명 진입점 IP당 제한 (브루트포스 방어)
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var userAgent = Request.Headers.UserAgent.ToString();

        // 자격증명 원문을 서비스에 전달 — 서비스가 PasswordHasher.Verify(salt+PBKDF2)로 검증한다(§19.2.2)
        var result = await _userService.ValidateAndLoginAsync(
            request.UserId, request.Password, ipAddress, userAgent, ct);

        if (result.IsFailure)
        {
            // §19.1.4/§20.10 — 401 응답을 code로 구분한다. 잠금은 안내가 필요하므로 메시지를 노출하고,
            // 자격 증명 오류는 계정 존재 여부를 드러내지 않는 동일 메시지를 유지한다.
            return result.Error.Code == "Auth.AccountLocked"
                ? Unauthorized(new { code = "ACCOUNT_LOCKED", message = result.Error.Description })
                : Unauthorized(new { code = "INVALID_CREDENTIALS", message = "Invalid credentials." });
        }

        var user = result.Value;
        var roles = new[] { user.RoleId };
        // §20.10 — Forgot/Create/Expired 상태는 DB 기준으로 변경 강제 + pwdChange 클레임으로 업무 API 차단
        var requireChange = user.RequiresPasswordChange;
        var permissions = await _userService.GetEffectivePermissionsAsync(user.RoleId, ct);   // ADR-003
        var accessToken = _jwtService.GenerateAccessToken(
            request.UserId, user.UserName, request.PlantId, roles, requireChange, permissions);
        var refreshToken = await _tokenStore.IssueAsync(request.UserId);

        return Ok(new LoginResponse(
            accessToken, refreshToken, request.UserId, user.UserName, request.PlantId, roles, requireChange));
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        // §19 — 폐기 대상 userId는 본문이 아니라 토큰에서 취한다. 본문 userId를 신뢰하면 임의 사용자의
        // 리프레시 토큰을 폐기하는 IDOR/DoS가 가능하므로, 인증 주체 본인의 토큰만 폐기하도록 강제한다.
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value ?? string.Empty;
        await _tokenStore.RevokeAsync(userId, request.RefreshToken);
        return NoContent();
    }

    [HttpPost("refresh")]
    [AllowAnonymous]               // 전역 FallbackPolicy 예외 — 액세스 토큰 만료 후 호출되는 갱신 진입점
    [EnableRateLimiting("auth")]   // §18.2.3 — 토큰 무차별 대입 방어
    // 200 본문은 익명 타입 { accessToken, refreshToken } — 명명 DTO가 없어 상태코드만 주석한다.
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        var isValid = await _tokenStore.ValidateAsync(request.UserId, request.RefreshToken);
        if (!isValid)
            return Unauthorized(new { message = "Invalid or expired refresh token." });

        // §20.10 — 변경 강제 여부와 역할은 DB 상태로 재평가한다. 구 액세스 토큰의 클레임을
        // 승계하는 방식은 Authorization 헤더 없이 갱신을 호출하면 클레임이 소실되어
        // pwdChange 차단을 우회할 수 있다. 비활성/삭제 사용자도 여기서 갱신이 끊긴다.
        var userResult = await _userService.GetUserAsync(request.UserId, ct);
        if (userResult.IsFailure || !userResult.Value.IsActive || userResult.Value.IsDeleted)
            return Unauthorized(new { message = "Invalid or expired refresh token." });

        var user = userResult.Value;
        var newRefreshToken = await _tokenStore.RotateAsync(request.UserId, request.RefreshToken);

        // plantId는 로그인 시 선택값이라 DB에 없다 — 구 토큰에서만 승계한다 (판정에는 사용하지 않음)
        var principal = _jwtService.ValidateAccessToken(
            HttpContext.Request.Headers.Authorization.ToString().Replace("Bearer ", ""));
        var plantId = principal?.FindFirst("plantId")?.Value ?? "DEFAULT";

        var permissions = await _userService.GetEffectivePermissionsAsync(user.RoleId, ct);   // ADR-003
        var accessToken = _jwtService.GenerateAccessToken(
            request.UserId, user.UserName, plantId, new[] { user.RoleId }, user.RequiresPasswordChange, permissions);
        return Ok(new { accessToken, refreshToken = newRefreshToken });
    }

    [HttpPost("change-password")]
    [Authorize]
    // 200 본문은 익명 타입 { accessToken, refreshToken } — 명명 DTO가 없어 상태코드만 주석한다.
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        if (request.NewPassword != request.ConfirmPassword)
            return BadRequest(new { message = "Passwords do not match." });

        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value ?? string.Empty;

        var userResult = await _userService.GetUserAsync(userId, ct);
        if (userResult.IsFailure)
            return BadRequest(userResult.Error);
        var user = userResult.Value;

        // §19.2.2 — 복잡도 정책 서버 최종 검증 (사용자 ID/이름/이메일 포함 금지 포함).
        var policyViolation = PasswordPolicy.Validate(request.NewPassword, userId, user.UserName, user.Email);
        if (policyViolation is not null)
            return BadRequest(new { code = PasswordPolicy.ErrorCode, message = policyViolation });

        // 자격증명 원문을 전달 — 서비스가 현재 비밀번호를 Verify하고 새 비밀번호를 강화 해시로 저장한다
        var result = await _userService.ChangePasswordAsync(
            userId, request.CurrentPassword, request.NewPassword, ct);

        if (result.IsFailure)
            return BadRequest(result.Error);

        // §19.2.4-7 — 변경 성공 시 기존 리프레시 토큰을 모두 폐기해 다른 기기 세션을 만료시킨다.
        // (Forgot 상태에서 발급된 리프레시 토큰이 변경 후에도 살아남으면 안 된다)
        await _tokenStore.RevokeAllByUserAsync(userId);

        // §20.10 — 변경 성공 시 pwdChange 클레임 없는 새 토큰을 재발급한다.
        // 이전 토큰은 만료까지 업무 API가 차단되므로 클라이언트는 응답 토큰으로 교체해야 한다.
        var plantId = User.FindFirst("plantId")?.Value ?? "DEFAULT";
        var roles = new[] { user.RoleId };
        var permissions = await _userService.GetEffectivePermissionsAsync(user.RoleId, ct);   // ADR-003
        var accessToken = _jwtService.GenerateAccessToken(userId, user.UserName, plantId, roles, permissions: permissions);
        var refreshToken = await _tokenStore.IssueAsync(userId);

        return Ok(new { accessToken, refreshToken });
    }

    [HttpPost("forgot-password")]
    [AllowAnonymous]               // 전역 FallbackPolicy 예외 — 비밀번호 분실 익명 진입점
    [EnableRateLimiting("auth")]   // §18.2.3 — 계정 열거/메일 폭주 방어
    // 202 본문은 익명 타입 { message } — 명명 DTO가 없어 상태코드만 주석한다.
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequest request, CancellationToken ct)
    {
        // §20.10 — 아이디/이메일이 틀려도 동일 응답 (계정 열거 방지). 상세 사유는 서버 로그에만 남는다.
        await _passwordResetService.ForgotPasswordAsync(request.UserId, request.Email, ct);
        return Accepted(new { message = "임시 비밀번호가 등록된 이메일로 발송되었습니다." });
    }

    // §20.10 — 구버전 reset-password 호환. 301 리다이렉트는 클라이언트가 따라갈 때
    // POST가 GET으로 바뀌고 본문이 소실되어 실제로 동작하지 않으므로, 서버 내부에서
    // forgot-password와 동일하게 위임 처리한다.
    [HttpPost("reset-password")]
    [AllowAnonymous]               // 전역 FallbackPolicy 예외 — forgot-password 호환 익명 진입점
    [EnableRateLimiting("auth")]   // §18.2.3 — forgot-password와 동일 정책
    // 202 본문은 익명 타입 { message } — 명명 DTO가 없어 상태코드만 주석한다.
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequest request, CancellationToken ct)
    {
        await _passwordResetService.ForgotPasswordAsync(request.UserId, request.Email, ct);
        return Accepted(new { message = "임시 비밀번호가 등록된 이메일로 발송되었습니다." });
    }

    [HttpGet("me")]
    [Authorize]
    // 200 본문은 익명 타입 { userId, userName, plantId, roles } — 명명 DTO가 없어 상태코드만 주석한다.
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        var userId = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
            ?? User.FindFirst("sub")?.Value;
        var userName = User.Identity?.Name;
        var plantId = User.FindFirst("plantId")?.Value;
        var roles = User.FindAll(System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).ToList();
        return Ok(new { userId, userName, plantId, roles });
    }
}
