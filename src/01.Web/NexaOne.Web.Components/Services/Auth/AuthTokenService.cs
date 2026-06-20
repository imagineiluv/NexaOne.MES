using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;

namespace NexaOne.Web.Services.Auth;

public sealed class AuthTokenService
{
    private const string AccessTokenKey  = "nexaone_access";
    private const string RefreshTokenKey = "nexaone_refresh";
    private const string UserIdKey       = "nexaone_uid";

    private readonly ProtectedSessionStorage _session;

    public AuthTokenService(ProtectedSessionStorage session)
    {
        _session = session;
    }

    public async Task SaveAsync(string accessToken, string refreshToken, string userId)
    {
        await _session.SetAsync(AccessTokenKey,  accessToken);
        await _session.SetAsync(RefreshTokenKey, refreshToken);
        await _session.SetAsync(UserIdKey,       userId);
    }

    public async Task<string?> GetAccessTokenAsync()
    {
        try
        {
            var result = await _session.GetAsync<string>(AccessTokenKey);
            return result.Success ? result.Value : null;
        }
        catch { return null; }
    }

    public async Task<string?> GetRefreshTokenAsync()
    {
        try
        {
            var result = await _session.GetAsync<string>(RefreshTokenKey);
            return result.Success ? result.Value : null;
        }
        catch { return null; }
    }

    public async Task<string?> GetUserIdAsync()
    {
        try
        {
            var result = await _session.GetAsync<string>(UserIdKey);
            return result.Success ? result.Value : null;
        }
        catch { return null; }
    }

    public async Task ClearAsync()
    {
        await _session.DeleteAsync(AccessTokenKey);
        await _session.DeleteAsync(RefreshTokenKey);
        await _session.DeleteAsync(UserIdKey);
    }
}
