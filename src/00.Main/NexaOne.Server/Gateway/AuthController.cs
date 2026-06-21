using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaOne.Application.Auth;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 인증 엔드포인트(게이트웨이식, 무-브리지). login/refresh만 구현(Phase 3b 범위).
/// 기존 NexaOne.API와 동일 라우트/DTO/상태코드/오류 코드. plugin↔DI 브리지 없이 Default-ALC + 격리 명명 쿼리로 동작.</summary>
[ApiController]
[Route("api/v1/auth")]
[ProducesErrorResponseType(typeof(Error))]
public sealed class AuthController : ControllerBase
{
    private readonly GatewayLoginService _login;
    private readonly IJwtService _jwt;
    private readonly IRefreshTokenStore _tokens;

    public AuthController(GatewayLoginService login, IJwtService jwt, IRefreshTokenStore tokens)
    {
        _login = login;
        _jwt = jwt;
        _tokens = tokens;
    }

    [HttpPost("login")]
    [AllowAnonymous]                 // 전역 인증 요구의 익명 예외 진입점
    [EnableRateLimiting("auth")]     // IP당 10/min — 브루트포스 방어
    [ProducesResponseType<LoginResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var ua = Request.Headers.UserAgent.ToString();
        var outcome = await _login.LoginAsync(request.UserId, request.Password, request.PlantId, ip, ua, ct);
        return outcome.Result;
    }

    [HttpPost("refresh")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    [ProducesResponseType<TokenRefreshResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest request, CancellationToken ct)
    {
        // plantId는 DB에 없으므로 구 Bearer 토큰에서만 승계(판정 미사용). 헤더 없으면 DEFAULT로 저하.
        var principal = _jwt.ValidateAccessToken(
            HttpContext.Request.Headers.Authorization.ToString().Replace("Bearer ", string.Empty));
        var plantId = principal?.FindFirst("plantId")?.Value;
        var outcome = await _login.RefreshAsync(request.UserId, request.RefreshToken, plantId, ct);
        return outcome.Result;
    }

    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest request)
    {
        // 폐기 대상 userId는 본문이 아니라 토큰에서 — 임의 사용자 토큰 폐기(IDOR/DoS) 방지.
        var userId = CurrentUserId;
        await _tokens.RevokeAsync(userId, request.RefreshToken);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    [ProducesResponseType<TokenRefreshResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        if (request.NewPassword != request.ConfirmPassword)
            return BadRequest(new Error("Auth.PasswordMismatch", "Passwords do not match.", ErrorType.Validation));

        var userId = CurrentUserId;
        var row = await _login.GetUserRowAsync(userId, ct);
        if (row is null)
            return BadRequest(new Error("USER_NOT_FOUND", "User not found.", ErrorType.Validation));

        // §19.2.2 서버 최종 정책 검증(userId/이름/이메일 포함 금지). 평문은 컨트롤러에만 존재.
        var userName = row.TryGetValue("USER_NAME", out var un) ? un?.ToString() : null;
        var email = row.TryGetValue("EMAIL", out var em) ? em?.ToString() : null;
        var violation = PasswordPolicy.Validate(request.NewPassword, userId, userName, email);
        if (violation is not null)
            return BadRequest(new Error(PasswordPolicy.ErrorCode, violation, ErrorType.Validation));

        var plantId = User.FindFirst("plantId")?.Value ?? "DEFAULT";
        var outcome = await _login.ChangePasswordAsync(userId, request.CurrentPassword, request.NewPassword, plantId, ct);
        return outcome.Result;
    }

    [HttpPost("register")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Register([FromBody] RegisterRequest request, CancellationToken ct)
    {
        if (!User.HasPermission(Permissions.SysManage)) return Forbid();

        var violation = PasswordPolicy.Validate(request.Password, request.UserId, request.UserName, request.Email);
        if (violation is not null)
            return BadRequest(new Error(PasswordPolicy.ErrorCode, violation, ErrorType.Validation));
        if (string.IsNullOrWhiteSpace(request.UserId) || string.IsNullOrWhiteSpace(request.RoleId))
            return BadRequest(new Error("INVALID_REGISTRATION", "UserId와 RoleId는 필수입니다.", ErrorType.Validation));

        var outcome = await _login.RegisterAsync(request, CurrentUserId, ct);
        return outcome.Result;
    }

    [HttpGet("me")]
    [Authorize]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Me()
    {
        var roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList();
        return Ok(new CurrentUserResponse(CurrentUserId, User.Identity?.Name, User.FindFirst("plantId")?.Value, roles));
    }

    private string CurrentUserId => User.CurrentUserId() ?? string.Empty;
}
