using System.Security.Cryptography;
using System.Text;
using NexaOne.Application.Auth;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;

namespace NexaOne.Server.Gateway;

/// <summary>DB 영속 리프레시 토큰 저장소(게이트웨이식, 무-브리지). 평문 대신 SHA-256 해시를 저장하고,
/// 회전은 '활성 토큰만 조건부 폐기' 영향행수로 재생 공격을 탐지한다(인메모리보다 강화). 격리 인증 레지스트리만 사용한다.</summary>
public sealed class SysRefreshTokenStore : IRefreshTokenStore
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _authQueries;
    private readonly IJwtService _jwt;
    private readonly TimeSpan _ttl;

    public SysRefreshTokenStore(IRuleDispatcher dispatcher, IQueryRegistry authQueries, IJwtService jwt, TimeSpan ttl)
    {
        _dispatcher = dispatcher;
        _authQueries = authQueries;
        _jwt = jwt;
        _ttl = ttl;
    }

    public async Task<string> IssueAsync(string userId)
    {
        var token = _jwt.GenerateRefreshToken();
        var now = DateTime.UtcNow;
        await _dispatcher.ExecuteAsync(Sql("SYS.InsertRefreshToken"), new Dictionary<string, object>
        {
            ["tokenId"] = Guid.NewGuid().ToString("N"),
            ["userId"] = userId,
            ["tokenHash"] = Hash(token),
            ["expiresAt"] = now.Add(_ttl),
            ["utcNow"] = now,
        });
        return token;
    }

    public async Task<bool> ValidateAsync(string userId, string token)
    {
        var rows = await _dispatcher.QueryAsync(Sql("SYS.ValidateRefreshToken"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["tokenHash"] = Hash(token),
            ["utcNow"] = DateTime.UtcNow,
        });
        return rows.Count > 0;
    }

    public async Task<string> RotateAsync(string userId, string oldToken)
    {
        // 활성 토큰만 조건부 폐기 — 영향행수 0이면 이미 폐기/만료(재생) → 빈 문자열로 회전 실패를 알린다.
        var affected = await _dispatcher.ExecuteAsync(Sql("SYS.RevokeRefreshTokenIfActive"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["tokenHash"] = Hash(oldToken),
            ["utcNow"] = DateTime.UtcNow,
        });
        if (affected == 0) return string.Empty;
        return await IssueAsync(userId);
    }

    public async Task RevokeAsync(string userId, string token)
        => await _dispatcher.ExecuteAsync(Sql("SYS.RevokeRefreshTokenIfActive"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["tokenHash"] = Hash(token),
            ["utcNow"] = DateTime.UtcNow,
        });

    public async Task RevokeAllByUserAsync(string userId)
        => await _dispatcher.ExecuteAsync(Sql("SYS.RevokeAllRefreshTokens"), new Dictionary<string, object>
        {
            ["userId"] = userId,
            ["utcNow"] = DateTime.UtcNow,
        });

    private string Sql(string id) => _authQueries.TryGet(id, out var def) && def is not null
        ? def.Sql
        : throw new InvalidOperationException($"인증 명명 쿼리 '{id}'가 격리 레지스트리에 없습니다(db/queries-auth/{_authQueries.Dialect}).");

    // 토큰은 평문 저장 금지 — SHA-256 hex로 저장/조회한다(불투명 난수 토큰이라 stretching 불필요, 인덱스 조회 등가).
    private static string Hash(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
