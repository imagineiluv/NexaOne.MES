using System.IdentityModel.Tokens.Jwt;
using NexaOne.Web.Services.Auth;

namespace NexaOne.Web.Services;

public sealed class AuthContextService
{
    private readonly AuthTokenService _tokenService;
    private static readonly JwtSecurityTokenHandler _handler = new();

    public AuthContextService(AuthTokenService tokenService) => _tokenService = tokenService;

    public async Task<string?> GetUserIdAsync()   => await GetClaimAsync("sub");
    public async Task<string?> GetUserNameAsync() => await GetClaimAsync("name");
    public async Task<string?> GetPlantIdAsync()  => await GetClaimAsync("plantId");

    private async Task<string?> GetClaimAsync(string claimType)
    {
        var token = await _tokenService.GetAccessTokenAsync();
        if (string.IsNullOrEmpty(token)) return null;
        try
        {
            var jwt = _handler.ReadJwtToken(token);
            return jwt.Claims.FirstOrDefault(c => c.Type == claimType)?.Value;
        }
        catch { return null; }
    }
}
