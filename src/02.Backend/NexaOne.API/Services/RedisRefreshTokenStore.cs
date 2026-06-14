using NexaOne.Driver.Redis;

namespace NexaOne.API.Services;

/// <summary>
/// 분산(Redis) 리프레시 토큰 저장소 — Jwt:RefreshTokenStore=Redis 일 때 인메모리(<see cref="RefreshTokenStore"/>)
/// 대신 쓴다. 다중 인스턴스/프로세스 재시작 간 세션·폐기 일관성이 필요할 때(스케일아웃) 활성화한다.
/// 토큰 유효성은 per-token 키(TTL로 자동 만료)로, 사용자 단위 폐기(RevokeAll)는 사용자 SET 멤버 열거로 처리한다.
/// </summary>
/// <remarks>
/// ⚠ opt-in 인프라 — 본 개발 환경엔 Redis 서버가 없어 런타임 미검증이다(<see cref="RedisCacheService"/>와 동일 상태).
/// 연결은 최초 사용 시 1회 lazy 수립한다.
/// </remarks>
public sealed class RedisRefreshTokenStore : IRefreshTokenStore
{
    private readonly RedisDriver _driver;
    private readonly IJwtService _jwt;
    private readonly string _connectionString;
    private readonly TimeSpan _ttl;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _connected;

    public RedisRefreshTokenStore(RedisDriver driver, IJwtService jwt, string connectionString, TimeSpan ttl)
    {
        _driver = driver;
        _jwt = jwt;
        _connectionString = connectionString;
        _ttl = ttl;
    }

    private async Task EnsureConnectedAsync()
    {
        if (_connected) return;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_connected)
            {
                await _driver.ConnectAsync(_connectionString).ConfigureAwait(false);
                _connected = true;
            }
        }
        finally { _gate.Release(); }
    }

    // 사용자 SET(폐기 열거용) · per-token 키(유효성·만료 TTL).
    private static string UserKey(string userId) => $"refresh:user:{userId}";
    private static string TokenKey(string userId, string token) => $"refresh:tok:{userId}:{token}";

    public async Task<string> IssueAsync(string userId)
    {
        await EnsureConnectedAsync();
        var token = _jwt.GenerateRefreshToken();
        await _driver.AddToSetAsync(UserKey(userId), token);
        await _driver.SetAsync(TokenKey(userId, token), "1", _ttl);
        return token;
    }

    public async Task<bool> ValidateAsync(string userId, string token)
    {
        await EnsureConnectedAsync();
        // per-token 키는 TTL로 만료되므로 '존재 = 유효'. (SET 멤버는 RevokeAll 열거용 부기일 뿐 유효성 기준 아님.)
        return await _driver.ExistsAsync(TokenKey(userId, token));
    }

    public async Task<string> RotateAsync(string userId, string oldToken)
    {
        await RevokeAsync(userId, oldToken);
        return await IssueAsync(userId);
    }

    public async Task RevokeAsync(string userId, string token)
    {
        await EnsureConnectedAsync();
        await _driver.RemoveFromSetAsync(UserKey(userId), token);
        await _driver.DeleteAsync(TokenKey(userId, token));
    }

    public async Task RevokeAllByUserAsync(string userId)
    {
        await EnsureConnectedAsync();
        // 사용자의 모든 per-token 키 삭제 후 SET 자체 삭제 — 폐기 즉시 전 세션 무효화(분산 일관).
        var tokens = await _driver.GetSetMembersAsync(UserKey(userId));
        foreach (var token in tokens)
            await _driver.DeleteAsync(TokenKey(userId, token));
        await _driver.DeleteAsync(UserKey(userId));
    }
}
