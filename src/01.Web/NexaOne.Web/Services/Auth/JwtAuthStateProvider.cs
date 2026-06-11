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

    private static ClaimsPrincipal? ParseToken(string token)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler();
            if (!handler.CanReadToken(token)) return null;

            var jwt = handler.ReadJwtToken(token);
            if (jwt.ValidTo < DateTime.UtcNow) return null;

            var identity = new ClaimsIdentity(jwt.Claims, "jwt");
            return new ClaimsPrincipal(identity);
        }
        catch
        {
            return null;
        }
    }
}
