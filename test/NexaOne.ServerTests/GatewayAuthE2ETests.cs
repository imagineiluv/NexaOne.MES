using System.Net;
using System.Net.Http.Json;
using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using NexaOne.Common;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>통합 호스트 인증 E2E(게이트웨이식 무-브리지) — modules OFF + SQLite(NexaMes 스키마, V034 포함).
/// 로그인 성공/열거방지/잠금패리티/잠금중-정답(SQLite 날짜 파싱)/refresh 회전·재생/권한클레임/rehash+폭확장을 검증한다.</summary>
public sealed class GatewayAuthE2ETests : IClassFixture<GatewayAuthE2ETests.AuthFactory>
{
    private const string Secret = "phase3b-auth-e2e-jwt-secret-key-at-least-32-bytes!!";
    private const string Issuer = "nexaone-auth-test";
    private readonly AuthFactory _factory;
    public GatewayAuthE2ETests(AuthFactory factory) => _factory = factory;

    public sealed class AuthFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-auth-e2e-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");   // 기능 테스트는 레이트리밋 비활성(공유 IP 비결정 회피)
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시파일 정리 실패 무시 */ }
        }
    }

    // 호스트를 한 번 띄워 SQLite 스키마(+admin 시드)를 보장한다(개발 SQLite 부트스트랩 경로).
    private void EnsureSchemaReady() => _ = _factory.CreateClient();

    private void SeedUser(string userId, string passwordHash, string roleId = "ADMIN",
        string passwordState = "Normal", int isActive = 1, int isDeleted = 0)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_USER
            (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE, IS_ACTIVE, IS_DELETED,
             PASSWORD_STATE, FAIL_COUNT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @id, @h, '', @role, 'KoKr', @act, @del, @ps, 0, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.Parameters.AddWithValue("@h", passwordHash);
        cmd.Parameters.AddWithValue("@role", roleId);
        cmd.Parameters.AddWithValue("@act", isActive);
        cmd.Parameters.AddWithValue("@del", isDeleted);
        cmd.Parameters.AddWithValue("@ps", passwordState);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private void SeedRole(string roleId, string permissions)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_ROLE (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @id, '', @perms, 0, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@id", roleId);
        cmd.Parameters.AddWithValue("@perms", permissions);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    private string? ReadPasswordHash(string userId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT PASSWORD_HASH FROM SYS_USER WHERE USER_ID = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        return cmd.ExecuteScalar() as string;
    }

    // 16자 = SYS_USER.USER_ID 예산. 접두사를 짧게 유지해야 Guid 엔트로피가 충분히 남는다(충돌 방지).
    private static string Uid(string p) => $"{p}_{Guid.NewGuid():N}".Substring(0, 16);

    [Fact]
    public async Task Login_succeeds_and_issues_tokens()
    {
        EnsureSchemaReady();
        var uid = Uid("ok");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("p@ssw0rd!"));
        var client = _factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = uid, password = "p@ssw0rd!", plantId = "P1" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<LoginBody>();
        body.Should().NotBeNull();
        body!.accessToken.Should().NotBeNullOrEmpty();
        body.refreshToken.Should().NotBeNullOrEmpty();
        body.userId.Should().Be(uid);
        body.plantId.Should().Be("P1");
    }

    [Fact]
    public async Task Login_nonexistent_user_returns_invalid_credentials_no_enumeration()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = Uid("ghost"), password = "x", plantId = "DEFAULT" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var err = await res.Content.ReadFromJsonAsync<ErrorBody>();
        err!.code.Should().Be("INVALID_CREDENTIALS", "존재하지 않는 사용자도 자격오류와 동일 코드(열거 방지)");
    }

    [Fact]
    public async Task Login_wrong_password_returns_invalid_then_locks_after_threshold()
    {
        EnsureSchemaReady();
        var uid = Uid("lock");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("correct!"));
        var client = _factory.CreateClient();

        for (var i = 0; i < 4; i++)
        {
            var bad = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "nope", plantId = "x" });
            bad.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            (await bad.Content.ReadFromJsonAsync<ErrorBody>())!.code.Should().Be("INVALID_CREDENTIALS");
        }
        var fifth = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "nope", plantId = "x" });
        fifth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await fifth.Content.ReadFromJsonAsync<ErrorBody>())!.code.Should().Be("ACCOUNT_LOCKED", "5회 연속 실패 시 잠금(패리티)");
    }

    [Fact]
    public async Task Login_with_correct_password_while_locked_is_rejected_sqlite_datetime_parse()
    {
        EnsureSchemaReady();
        var uid = Uid("lkok");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("correct!"));
        var client = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
            await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "nope", plantId = "x" });

        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "correct!", plantId = "x" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await res.Content.ReadFromJsonAsync<ErrorBody>())!.code.Should().Be("ACCOUNT_LOCKED",
            "잠금 중에는 정답이어도 거부돼야 한다(LOCKED_UNTIL 파싱 정상 입증)");
    }

    [Fact]
    public async Task Refresh_rotates_and_old_token_replay_is_rejected()
    {
        EnsureSchemaReady();
        var uid = Uid("rot");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("pw1"));
        var client = _factory.CreateClient();
        var login = await (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = uid, password = "pw1", plantId = "x" })).Content.ReadFromJsonAsync<LoginBody>();

        var r1 = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { userId = uid, refreshToken = login!.refreshToken });
        r1.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await r1.Content.ReadFromJsonAsync<RefreshBody>();
        rotated!.refreshToken.Should().NotBe(login.refreshToken, "회전으로 새 refresh 토큰이 발급돼야 한다");

        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { userId = uid, refreshToken = login.refreshToken });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var r2 = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { userId = uid, refreshToken = rotated.refreshToken });
        r2.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Login_issues_permission_claims_and_token_is_accepted_by_gateway()
    {
        EnsureSchemaReady();
        var roleId = Uid("ROLE");
        SeedRole(roleId, "mdm:manage");
        var uid = Uid("perm");
        SeedUser(uid, NexaOne.Common.PasswordHasher.Hash("pw2"), roleId: roleId);
        var client = _factory.CreateClient();

        var body = await (await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = uid, password = "pw2", plantId = "x" })).Content.ReadFromJsonAsync<LoginBody>();

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(body!.accessToken);
        jwt.Claims.Should().Contain(c =>
            c.Type == NexaOne.Common.Security.Permissions.ClaimType && c.Value == "mdm:manage");

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", body.accessToken);
        var save = await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        { ["plantId"] = "AUTH_" + Guid.NewGuid().ToString("N")[..6], ["plantName"] = "auth e2e" });
        save.StatusCode.Should().Be(HttpStatusCode.OK, "호스트 발급 토큰이 동일 호스트 JWT 검증을 통과해야 한다");
    }

    [Fact]
    public async Task Legacy_sha256_login_rehashes_to_pbkdf2()
    {
        EnsureSchemaReady();
        var uid = Uid("rehash");
        var legacy = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("legacy!"))).ToLowerInvariant();
        SeedUser(uid, legacy);
        var client = _factory.CreateClient();

        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = uid, password = "legacy!", plantId = "x" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, "레거시 SHA-256도 로그인 성공해야 한다");

        var stored = ReadPasswordHash(uid);
        stored.Should().StartWith("pbkdf2$", "로그인 성공 시 강화 해시로 재해싱돼야 한다");
        stored!.Length.Should().BeGreaterThan(64, "PBKDF2 해시(~83자)가 저장됨(MSSQL이면 PASSWORD_HASH 폭 확장 필요; SQLite는 길이 무개념)");
    }

    private sealed record LoginBody(string accessToken, string refreshToken, string userId, string userName,
        string plantId, List<string> roles, bool requirePasswordChange);
    private sealed record RefreshBody(string accessToken, string refreshToken);
    // 오류 응답은 code만 검증한다. 서버는 type을 문자열 열거(JsonStringEnumConverter)로 직렬화하나,
    // 기본 웹 역직렬화기에는 해당 컨버터가 없어 NexaOne.Common.Error 직접 역직렬화는 실패한다(테스트 한정 이슈).
    private sealed record ErrorBody(string code);
}
