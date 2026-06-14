using System.IdentityModel.Tokens.Jwt;
using NexaOne.Web.Services.Auth;

namespace NexaOne.Web.Services;

/// <summary>현재 사용자 컨텍스트(토큰 클레임) 접근 계약. 페이지가 이 계약에 의존하면 테스트에서 대체 가능하다
/// (구상 AuthContextService는 sealed + ProtectedSessionStorage 의존이라 직접 모킹 불가).</summary>
public interface IAuthContext
{
    Task<string?> GetUserIdAsync();
    Task<string?> GetUserNameAsync();
    Task<string?> GetPlantIdAsync();
}

public sealed class AuthContextService : IAuthContext
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
