using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

namespace NexaOne.Web.Services.Auth;

public sealed class JwtAuthStateProvider : AuthenticationStateProvider
{
    private readonly AuthTokenService _tokenService;
    private static readonly AuthenticationState Anonymous =
        new(new ClaimsPrincipal(new ClaimsIdentity()));

    public JwtAuthStateProvider(AuthTokenService tokenService)
    {
        _tokenService = tokenService;
    }

    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await _tokenService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token))
            return Anonymous;

        var principal = ParseToken(token);
        return principal is null ? Anonymous : new AuthenticationState(principal);
    }

    public void NotifyAuthChanged(ClaimsPrincipal? user)
    {
        var state = user is null
            ? Anonymous
            : new AuthenticationState(user);
        NotifyAuthenticationStateChanged(Task.FromResult(state));
    }

    // internal — 토큰 파싱 단위 테스트용(AuthTokenService가 sealed/JS 의존이라 GetAuthenticationStateAsync 직접 테스트 불가).
    internal static ClaimsPrincipal? ParseToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token)) return null;

            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo < DateTime.UtcNow) return null;

            // nameType="name"(API JwtService가 JwtRegisteredClaimNames.Name으로 발급) — 명시하지 않으면 기본
            // NameClaimType(ClaimTypes.Name URI)과 불일치해 Identity.Name이 항상 null이 된다(상단바 사용자명 공백).
            // roleType은 현행대로 ClaimTypes.Role(API가 그 타입으로 발급) 유지 — 역할 인가 동작 불변.
            var identity = new ClaimsIdentity(jwt.Claims, "jwt", JwtRegisteredClaimNames.Name, ClaimTypes.Role);
            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return null;
        }
    }
}
