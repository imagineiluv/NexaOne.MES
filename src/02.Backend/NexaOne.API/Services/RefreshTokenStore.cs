using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace NexaOne.API.Services;

public class RefreshTokenStore : IRefreshTokenStore
{
    private record TokenEntry(string Token, DateTime ExpiresAt);

    private readonly ConcurrentDictionary<string, List<TokenEntry>> _store = new();
    private readonly IJwtService _jwtService;
    private readonly TimeSpan _ttl;

    public RefreshTokenStore(IJwtService jwtService, IConfiguration config)
    {
        _jwtService = jwtService;
        _ttl = TimeSpan.FromDays(config.GetValue("Jwt:RefreshTokenExpiryDays", 7));
    }

    public Task<string> IssueAsync(string userId)
    {
        var token = _jwtService.GenerateRefreshToken();
        var entry = new TokenEntry(token, DateTime.UtcNow.Add(_ttl));
        var list = _store.GetOrAdd(userId, _ => new List<TokenEntry>());
        lock (list)
        {
            list.RemoveAll(e => e.ExpiresAt <= DateTime.UtcNow);   // 만료 토큰 정리
            list.Add(entry);
        }
        return Task.FromResult(token);
    }

    public Task<bool> ValidateAsync(string userId, string token)
    {
        if (!_store.TryGetValue(userId, out var list)) return Task.FromResult(false);
        lock (list)
        {
            var entry = list.Find(e => TokensEqual(e.Token, token));
            return Task.FromResult(entry != null && entry.ExpiresAt > DateTime.UtcNow);
        }
    }

    public async Task<string> RotateAsync(string userId, string oldToken)
    {
        await RevokeAsync(userId, oldToken);
        return await IssueAsync(userId);
    }

    public Task RevokeAsync(string userId, string token)
    {
        if (_store.TryGetValue(userId, out var list))
            lock (list) list.RemoveAll(e => TokensEqual(e.Token, token));
        return Task.CompletedTask;
    }

    public Task RevokeAllByUserAsync(string userId)
    {
        _store.TryRemove(userId, out _);
        return Task.CompletedTask;
    }

    // 리프레시 토큰 비교는 상수시간으로 수행해 타이밍 부채널(길이/접두부 일치로 일치 토큰 추정)을 차단한다.
    // FixedTimeEquals는 길이가 다르면 즉시 false이므로, UTF-8 바이트로 변환해 동일 길이 비교 경로로 위임한다.
    private static bool TokensEqual(string a, string b)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
