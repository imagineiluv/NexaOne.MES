using System.Globalization;
using NexaOne.Application.Auth;
using NexaOne.Application.Messaging;
using NexaOne.Application.Query;
using NexaOne.Common;
using NexaOne.Common.Security;

namespace NexaOne.Server.Gateway;

/// <summary>통합 호스트 인증 서비스(게이트웨이식, 무-브리지). 운영 UserService.ValidateAndLoginAsync +
/// AuthController.Login/Refresh의 동작을 Default-ALC 타입 + 격리 명명 쿼리로 재현한다.</summary>
public sealed class GatewayLoginService
{
    private readonly IRuleDispatcher _dispatcher;
    private readonly IQueryRegistry _authQueries;
    private readonly IJwtService _jwt;
    private readonly IRefreshTokenStore _tokenStore;

    public GatewayLoginService(IRuleDispatcher dispatcher, IQueryRegistry authQueries,
        IJwtService jwt, IRefreshTokenStore tokenStore)
    {
        _dispatcher = dispatcher;
        _authQueries = authQueries;
        _jwt = jwt;
        _tokenStore = tokenStore;
    }

    public async Task<AuthOutcome> LoginAsync(
        string userId, string password, string plantId, string ip, string ua, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);

        if (row is null)
        {
            await RecordFailureAsync(userId, ip, ua, LoginFailureReasons.UserNotFound, now, ct);
            return AuthOutcome.InvalidCredentials();
        }

        var lockedUntil = ToNullableDateTime(Get(row, "LOCKED_UNTIL"));
        if (lockedUntil is { } locked && locked > now)
        {
            await RecordFailureAsync(userId, ip, ua, LoginFailureReasons.AccountLocked, now, ct);
            return AuthOutcome.AccountLocked(locked, now);
        }

        if (!ToBool(Get(row, "IS_ACTIVE")) || ToBool(Get(row, "IS_DELETED")))
        {
            await RecordFailureAsync(userId, ip, ua, LoginFailureReasons.InactiveUser, now, ct);
            return AuthOutcome.InvalidCredentials();
        }

        var storedHash = ToStr(Get(row, "PASSWORD_HASH"));
        if (!PasswordHasher.Verify(password, storedHash))
        {
            await ExecuteAsync("SYS.RecordLoginFailure", new()
            {
                ["userId"] = userId,
                ["utcNow"] = now,
                ["maxFailures"] = AccountLockoutPolicy.MaxConsecutiveFailures,
                ["lockUntil"] = now.Add(AccountLockoutPolicy.LockDuration),
            }, ct);
            var afterRow = await QuerySingleAsync("SYS.GetLockedUntil", new() { ["userId"] = userId }, ct);
            var afterLock = ToNullableDateTime(afterRow is null ? null : Get(afterRow, "LOCKED_UNTIL"));
            await RecordFailureAsync(userId, ip, ua, LoginFailureReasons.WrongPassword, now, ct);
            return afterLock is { } until && until > now
                ? AuthOutcome.AccountLocked(until, now)
                : AuthOutcome.InvalidCredentials();
        }

        // 성공 — rehash-on-login(구 해시면 강화 해시 저장), 단일 UPDATE로 LAST_LOGIN_AT·실패카운터·잠금 처리.
        var rehash = PasswordHasher.NeedsRehash(storedHash) ? PasswordHasher.Hash(password) : null;
        await ExecuteAsync("SYS.RecordLoginSuccess", new()
        {
            ["userId"] = userId,
            ["utcNow"] = now,
            ["passwordHash"] = (object?)rehash ?? DBNull.Value,
        }, ct);

        var userName = ToStr(Get(row, "USER_NAME"));
        var roleId = ToStr(Get(row, "ROLE_ID"));
        var requireChange = !string.Equals(ToStr(Get(row, "PASSWORD_STATE"), "Normal"), "Normal", StringComparison.Ordinal);
        var roles = new[] { roleId };
        var perms = EffectivePermissions(roleId, ToNullableStr(Get(row, "PERMISSIONS")));
        var accessToken = _jwt.GenerateAccessToken(userId, userName, plantId, roles, requireChange, perms);
        var refreshToken = await _tokenStore.IssueAsync(userId);

        return AuthOutcome.Ok(new LoginResponse(
            accessToken, refreshToken, userId, userName, plantId, roles, requireChange));
    }

    public async Task<AuthOutcome> RefreshAsync(string userId, string refreshToken, string? bearerPlantId, CancellationToken ct)
    {
        if (!await _tokenStore.ValidateAsync(userId, refreshToken))
            return AuthOutcome.InvalidRefreshToken();

        // 역할/변경강제/활성·삭제는 DB 상태로 재평가한다(구 토큰 클레임 승계 금지 — pwdChange 우회 방지).
        var row = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);
        if (row is null || !ToBool(Get(row, "IS_ACTIVE")) || ToBool(Get(row, "IS_DELETED")))
            return AuthOutcome.InvalidRefreshToken();

        var newRefresh = await _tokenStore.RotateAsync(userId, refreshToken);
        if (string.IsNullOrEmpty(newRefresh))
            return AuthOutcome.InvalidRefreshToken();   // 회전 경합/재생 — 패배 측은 무효

        var userName = ToStr(Get(row, "USER_NAME"));
        var roleId = ToStr(Get(row, "ROLE_ID"));
        var requireChange = !string.Equals(ToStr(Get(row, "PASSWORD_STATE"), "Normal"), "Normal", StringComparison.Ordinal);
        var perms = EffectivePermissions(roleId, ToNullableStr(Get(row, "PERMISSIONS")));
        var plantId = string.IsNullOrEmpty(bearerPlantId) ? "DEFAULT" : bearerPlantId;
        var accessToken = _jwt.GenerateAccessToken(userId, userName, plantId, new[] { roleId }, requireChange, perms);

        return AuthOutcome.Ok(new TokenRefreshResponse(accessToken, newRefresh));
    }

    /// <summary>현재 사용자 1행 조회(컨트롤러가 me/권한 표시에 사용). 없으면 null.</summary>
    public Task<Dictionary<string, object?>?> GetUserRowAsync(string userId, CancellationToken ct)
        => QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);

    /// <summary>비밀번호 변경 — 현재 비번 검증 후 강화 해시 저장, 전 토큰 폐기, 새 토큰 재발급(pwdChange 없음).
    /// 정책 검증은 컨트롤러(평문 보유)가 선행한다. 본인 userId는 토큰에서 전달받는다.</summary>
    public async Task<AuthOutcome> ChangePasswordAsync(string userId, string currentPassword, string newPassword,
        string plantId, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var row = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = userId }, ct);
        if (row is null || ToBool(Get(row, "IS_DELETED")))
            return AuthOutcome.BadRequest(new Error("USER_NOT_FOUND", "User not found.", ErrorType.Validation));

        if (!PasswordHasher.Verify(currentPassword, ToStr(Get(row, "PASSWORD_HASH"))))
            return AuthOutcome.BadRequest(new Error("INVALID_CURRENT_PASSWORD", "현재 비밀번호가 올바르지 않습니다.", ErrorType.Validation));

        await ExecuteAsync("SYS.UpdatePassword", new()
        {
            ["userId"] = userId,
            ["passwordHash"] = PasswordHasher.Hash(newPassword),
            ["utcNow"] = now,
        }, ct);

        await _tokenStore.RevokeAllByUserAsync(userId);   // §19.2.4-7 다른 기기 세션 만료

        var userName = ToStr(Get(row, "USER_NAME"));
        var roleId = ToStr(Get(row, "ROLE_ID"));
        var perms = EffectivePermissions(roleId, ToNullableStr(Get(row, "PERMISSIONS")));
        // requireChange=false(변경 완료) 새 토큰 — pwdChange 클레임 제거.
        var accessToken = _jwt.GenerateAccessToken(userId, userName, plantId, new[] { roleId }, false, perms);
        var refreshToken = await _tokenStore.IssueAsync(userId);
        return AuthOutcome.Ok(new TokenRefreshResponse(accessToken, refreshToken));
    }

    /// <summary>관리자 사용자 등록 — 중복 검사 후 INSERT(PASSWORD_STATE='Create'=최초 변경 강제). 권한 게이트는 컨트롤러.
    /// 정책 검증은 컨트롤러가 선행. createdBy는 등록 수행 관리자 userId.</summary>
    public async Task<AuthOutcome> RegisterAsync(RegisterRequest req, string createdBy, CancellationToken ct)
    {
        var existing = await QuerySingleAsync("SYS.AuthUserById", new() { ["userId"] = req.UserId }, ct);
        if (existing is not null)
            return AuthOutcome.Conflict(new Error("USER_ALREADY_EXISTS", $"User '{req.UserId}' already exists.", ErrorType.Conflict));

        await ExecuteAsync("SYS.InsertUser", new()
        {
            ["userId"] = req.UserId,
            ["userName"] = req.UserName,
            ["passwordHash"] = PasswordHasher.Hash(req.Password),
            ["email"] = req.Email,
            ["roleId"] = req.RoleId,
            ["language"] = string.IsNullOrWhiteSpace(req.Language) ? "KoKr" : req.Language,
            ["currentUser"] = createdBy,
            ["utcNow"] = DateTime.UtcNow,
        }, ct);
        return AuthOutcome.Ok(new { userId = req.UserId });
    }

    // ── 권한 합성 (운영 UserService.GetEffectivePermissionsAsync와 동일: 기본 매핑 ∪ split('|'), OrdinalIgnoreCase distinct) ──
    private static IReadOnlyList<string> EffectivePermissions(string roleId, string? permissionsCsv)
    {
        var set = new HashSet<string>(RolePermissionDefaults.For(roleId), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(permissionsCsv))
            foreach (var p in permissionsCsv.Split('|', StringSplitOptions.RemoveEmptyEntries))
                set.Add(p);
        return set.ToList();
    }

    private async Task RecordFailureAsync(string userId, string ip, string ua, string reason, DateTime now, CancellationToken ct)
        => await ExecuteAsync("SYS.InsertLoginFailureHist", new()
        {
            ["failureId"] = Guid.NewGuid().ToString("N"),
            ["userId"] = Truncate(userId, 50),
            ["ipAddress"] = Truncate(ip, 45),
            ["userAgent"] = Truncate(ua, 500),
            ["failureReason"] = Truncate(reason, 50),
            ["utcNow"] = now,
        }, ct);

    private async Task<Dictionary<string, object?>?> QuerySingleAsync(
        string id, Dictionary<string, object> p, CancellationToken ct)
    {
        var rows = await _dispatcher.QueryAsync(_authQueries.Sql(id), p, ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private async Task ExecuteAsync(string id, Dictionary<string, object> p, CancellationToken ct)
        => await _dispatcher.ExecuteAsync(_authQueries.Sql(id), p, ct);

    // 컬럼 누락 시 null 반환 — 쿼리 오타/스키마 변경은 fail-closed로 흐른다(예: PASSWORD_HASH 누락→""→Verify 실패). 설계상 의도.
    private static object? Get(IReadOnlyDictionary<string, object?> row, string col)
        => row.TryGetValue(col, out var v) ? v : null;

    // ── DB 방언 차이 흡수(MSSQL: bool/DateTime, SQLite: long/string) ──
    private static bool ToBool(object? v) => v switch
    {
        null or DBNull => false,
        bool b => b,
        long l => l != 0,
        int i => i != 0,
        string s => s == "1" || string.Equals(s, "true", StringComparison.OrdinalIgnoreCase),
        _ => Convert.ToInt64(v) != 0,
    };

    private static string ToStr(object? v, string fallback = "") => v switch
    {
        null or DBNull => fallback,
        string s => s,
        _ => v.ToString() ?? fallback,
    };

    private static string? ToNullableStr(object? v) => v switch
    {
        null or DBNull => null,
        string s => s,
        _ => v.ToString(),
    };

    // 잠금시각 파싱 — MSSQL DATETIME2→DateTime, SQLite TEXT(ISO8601)→파싱. 보안상 파싱 실패는 null(잠금 미인정)이
    // 되지 않도록, 실패 시 SQL측 검증(실패경로 재잠금)에 의존하되 통합테스트로 SQLite 파싱을 직접 검증한다.
    private static DateTime? ToNullableDateTime(object? v) => v switch
    {
        null or DBNull => null,
        DateTime dt => dt,
        string s => DateTime.TryParse(s, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var p) ? p : null,
        _ => null,
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
