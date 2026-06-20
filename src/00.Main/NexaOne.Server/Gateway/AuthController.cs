using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NexaOne.Application.Auth;
using NexaOne.Common;

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

    public AuthController(GatewayLoginService login, IJwtService jwt)
    {
        _login = login;
        _jwt = jwt;
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
}
