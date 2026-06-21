using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>트랙① 인증 완결 E2E(게이트웨이식 무-브리지) — logout(폐기)·change-password(성공/검증실패)·
/// register(중복/권한)·me 를 통합 호스트 + SQLite(admin/admin 부트스트랩 시드)로 검증한다.
/// GatewayAuthE2ETests 팩토리 구조를 복제하되, SYS_USER를 변이하는 시나리오는 각자 고유 SQLite DB로 격리해
/// admin/admin 로그인이 다른 시나리오에서 깨지지 않게 한다(시나리오별 IClassFixture 분리).</summary>
public static class GatewayAuthCompletenessTests
{
    private const string Secret = "track1-auth-completeness-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-auth-completeness-test";

    // ── 시나리오별 팩토리(고유 DB파일) — admin 시드 격리 ──────────────────────────────
    // 클래스명·DB파일 접두사를 분리해 GatewayAuthE2ETests와 충돌 없이 별도 호스트/스키마를 띄운다.

    public sealed class LogoutFactory : AuthCompletenessFactoryBase { }
    public sealed class ChangePwSuccessFactory : AuthCompletenessFactoryBase { }
    public sealed class ChangePwValidationFactory : AuthCompletenessFactoryBase { }
    public sealed class RegisterFactory : AuthCompletenessFactoryBase { }
    public sealed class MeFactory : AuthCompletenessFactoryBase { }

    public abstract class AuthCompletenessFactoryBase : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-auth-complete-{Guid.NewGuid():N}.db");
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
            builder.UseSetting("RateLimiting:Enabled", "false");   // 다회 로그인 시 레이트리밋 비결정 회피
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시파일 정리 실패 무시 */ }
        }
    }

    // ── 공용 헬퍼 ──────────────────────────────────────────────────────────────────

    /// <summary>admin/admin 로그인 → 토큰 발급(개발 SQLite 부트스트랩의 V001 admin 시드, 비번 'admin').</summary>
    private static async Task<LoginBody> LoginAdminAsync(HttpClient client, string password = "admin")
    {
        var res = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { userId = "admin", password, plantId = "DEFAULT" });
        res.StatusCode.Should().Be(HttpStatusCode.OK, "admin/admin 부트스트랩 시드로 로그인 성공해야 한다");
        var body = await res.Content.ReadFromJsonAsync<LoginBody>();
        body.Should().NotBeNull();
        return body!;
    }

    private static void SetBearer(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    /// <summary>팩토리 JWT 파라미터(Secret/Issuer/Audience)와 일치하는 토큰을 직접 민팅한다 — 권한(403) 케이스용.
    /// permission 클레임에 'sys:manage'/'*'를 넣지 않으면 register가 403이어야 한다.</summary>
    private static string MintToken(string userId, params string[] permissions)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims,
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // ── 1) logout: refreshToken 폐기 후 같은 토큰으로 refresh→401 ───────────────────
    public sealed class Logout : IClassFixture<LogoutFactory>
    {
        private readonly LogoutFactory _factory;
        public Logout(LogoutFactory factory) => _factory = factory;

        [Fact]
        public async Task Logout_revokes_refresh_token_then_refresh_is_rejected()
        {
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);

            SetBearer(client, login.accessToken);
            var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = login.refreshToken });
            logout.StatusCode.Should().Be(HttpStatusCode.NoContent, "로그아웃은 204를 반환해야 한다");

            // 폐기된 refreshToken은 더 이상 회전에 사용할 수 없다.
            var refresh = await client.PostAsJsonAsync("/api/v1/auth/refresh",
                new { userId = "admin", refreshToken = login.refreshToken });
            refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "폐기된 refresh 토큰은 401이어야 한다");
        }
    }

    // ── 2) change-password 성공: 새 비번 재로그인 성공, 구 비번 401 (전용 DB) ─────────
    public sealed class ChangePasswordSuccess : IClassFixture<ChangePwSuccessFactory>
    {
        private readonly ChangePwSuccessFactory _factory;
        public ChangePasswordSuccess(ChangePwSuccessFactory factory) => _factory = factory;

        [Fact]
        public async Task ChangePassword_succeeds_then_relogin_with_new_and_old_is_rejected()
        {
            const string newPw = "NewP@ssw0rd!";
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);

            SetBearer(client, login.accessToken);
            var change = await client.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = "admin", newPassword = newPw, confirmPassword = newPw });
            change.StatusCode.Should().Be(HttpStatusCode.OK, "유효한 변경은 200(TokenRefreshResponse)이어야 한다");
            var tokens = await change.Content.ReadFromJsonAsync<RefreshBody>();
            tokens.Should().NotBeNull();
            tokens!.accessToken.Should().NotBeNullOrEmpty();
            tokens.refreshToken.Should().NotBeNullOrEmpty();

            // 새 비번으로 재로그인 성공.
            var fresh = _factory.CreateClient();
            var relogin = await fresh.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "admin", password = newPw, plantId = "DEFAULT" });
            relogin.StatusCode.Should().Be(HttpStatusCode.OK, "변경된 비번으로 재로그인 성공해야 한다");

            // 구 비번 'admin' 로그인은 거부.
            var old = await fresh.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "admin", password = "admin", plantId = "DEFAULT" });
            old.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "구 비번으로는 로그인할 수 없어야 한다");
        }
    }

    // ── 3) change-password 검증 실패: 현재비번오류/정책위반/불일치 (admin 비번 미변경) ──
    public sealed class ChangePasswordValidation : IClassFixture<ChangePwValidationFactory>
    {
        private readonly ChangePwValidationFactory _factory;
        public ChangePasswordValidation(ChangePwValidationFactory factory) => _factory = factory;

        [Fact]
        public async Task ChangePassword_wrong_current_returns_INVALID_CURRENT_PASSWORD()
        {
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);
            SetBearer(client, login.accessToken);

            var res = await client.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = "wrong", newPassword = "NewP@ssw0rd!", confirmPassword = "NewP@ssw0rd!" });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await res.Content.ReadFromJsonAsync<ErrorBody>())!.code.Should().Be("INVALID_CURRENT_PASSWORD");
        }

        [Fact]
        public async Task ChangePassword_weak_new_returns_PASSWORD_POLICY_VIOLATION()
        {
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);
            SetBearer(client, login.accessToken);

            var res = await client.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = "admin", newPassword = "weak", confirmPassword = "weak" });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await res.Content.ReadFromJsonAsync<ErrorBody>())!.code.Should().Be("PASSWORD_POLICY_VIOLATION");
        }

        [Fact]
        public async Task ChangePassword_mismatch_returns_Auth_PasswordMismatch()
        {
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);
            SetBearer(client, login.accessToken);

            var res = await client.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = "admin", newPassword = "NewP@ssw0rd!", confirmPassword = "Different@1!" });
            res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await res.Content.ReadFromJsonAsync<ErrorBody>())!.code.Should().Be("Auth.PasswordMismatch");
        }

        [Fact]
        public async Task Admin_password_unchanged_after_validation_failures()
        {
            // 위 실패 경로들은 비번을 변경하지 않아야 한다 — admin/admin 재로그인이 여전히 성공.
            var client = _factory.CreateClient();
            var relogin = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "admin", password = "admin", plantId = "DEFAULT" });
            relogin.StatusCode.Should().Be(HttpStatusCode.OK, "검증 실패 경로는 비번을 변경하지 않아야 한다");
        }
    }

    // ── 4) register: 신규(200·requirePasswordChange)/중복(409)/권한미보유(403) ──────
    public sealed class Register : IClassFixture<RegisterFactory>
    {
        private readonly RegisterFactory _factory;
        public Register(RegisterFactory factory) => _factory = factory;

        [Fact]
        public async Task Register_with_admin_succeeds_new_user_requires_password_change_and_duplicate_conflicts()
        {
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);
            SetBearer(client, login.accessToken);

            // roleId='ADMIN' = 부트스트랩 시드된 활성 SYS_ROLE(SEC-1 검증 통과). OPERATOR는 시드되지 않아 INVALID_ROLE이 된다.
            var reg = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { userId = "u1", userName = "U1", password = "Reg!Pass99", email = "u1@x.com", roleId = "ADMIN" });
            reg.StatusCode.Should().Be(HttpStatusCode.OK, "admin(ADMIN 역할='*' 권한)으로 등록은 200이어야 한다");

            // 신규 u1 로그인 → PASSWORD_STATE='Create'라 requirePasswordChange=true.
            var u1Client = _factory.CreateClient();
            var u1Login = await u1Client.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "u1", password = "Reg!Pass99", plantId = "DEFAULT" });
            u1Login.StatusCode.Should().Be(HttpStatusCode.OK, "등록한 u1로 로그인 성공해야 한다");
            var u1Body = await u1Login.Content.ReadFromJsonAsync<LoginBody>();
            u1Body!.requirePasswordChange.Should().BeTrue("신규 등록 사용자는 PASSWORD_STATE='Create'로 최초 변경 강제");

            // 같은 u1 재등록 → 409.
            var dup = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { userId = "u1", userName = "U1", password = "Reg!Pass99", email = "u1@x.com", roleId = "ADMIN" });
            dup.StatusCode.Should().Be(HttpStatusCode.Conflict, "중복 userId 등록은 409여야 한다");
        }

        [Fact]
        public async Task Register_without_sys_manage_is_forbidden()
        {
            // sys:manage/* 미보유 토큰(다른 권한만) → 403.
            var client = _factory.CreateClient();
            SetBearer(client, MintToken("nopermuser", "fdc:read"));

            var res = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { userId = "u2", userName = "U2", password = "Reg!Pass99", email = "u2@x.com", roleId = "OPERATOR" });
            res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "sys:manage 권한 미보유 시 register는 403이어야 한다");
        }

        // SEC-1: SYS_USER.ROLE_ID FK 부재 보완 — 존재하지 않는(또는 비활성) 역할로 등록 시 400 INVALID_ROLE,
        // 그리고 SYS_USER 행이 생성되지 않아야 한다(orphan/무력 계정·하드코딩 권한 디커플링 차단).
        [Fact]
        public async Task Register_with_nonexistent_role_returns_INVALID_ROLE_and_inserts_no_user()
        {
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);
            SetBearer(client, login.accessToken);

            const string ghostUser = "ghost_role_user";
            var res = await client.PostAsJsonAsync("/api/v1/auth/register",
                new { userId = ghostUser, userName = "Ghost", password = "Reg!Pass99", email = "g@x.com",
                      roleId = "NO_SUCH_ROLE" });

            res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "존재하지 않는 역할로 등록은 400이어야 한다(SEC-1)");
            (await res.Content.ReadFromJsonAsync<ErrorBody>())!.code.Should().Be("INVALID_ROLE");

            CountUsers(ghostUser).Should().Be(0, "검증 실패 시 SYS_USER 행이 생성되지 않아야 한다");
        }

        // 검증 실패가 INSERT를 막았는지 직접 확인(테스트 DB는 격리 SQLite 파일).
        private int CountUsers(string userId)
        {
            using var conn = new SqliteConnection(_factory.ConnString);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM SYS_USER WHERE USER_ID = @id";
            cmd.Parameters.AddWithValue("@id", userId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    // ── 5) me: 인증 토큰으로 현재 사용자 조회 → userId=admin ──────────────────────────
    public sealed class Me : IClassFixture<MeFactory>
    {
        private readonly MeFactory _factory;
        public Me(MeFactory factory) => _factory = factory;

        [Fact]
        public async Task Me_returns_current_user_identity()
        {
            var client = _factory.CreateClient();
            var login = await LoginAdminAsync(client);
            SetBearer(client, login.accessToken);

            var res = await client.GetAsync("/api/v1/auth/me");
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            var body = await res.Content.ReadFromJsonAsync<MeBody>();
            body.Should().NotBeNull();
            body!.userId.Should().Be("admin");
        }
    }

    // ── 응답 형태(camelCase 기본 직렬화) ──────────────────────────────────────────────
    private sealed record LoginBody(string accessToken, string refreshToken, string userId, string userName,
        string plantId, List<string> roles, bool requirePasswordChange);
    private sealed record RefreshBody(string accessToken, string refreshToken);
    private sealed record MeBody(string? userId, string? userName, string? plantId, List<string>? roles);
    // 오류 응답은 code만 검증한다(GatewayAuthE2ETests와 동일 — type enum 컨버터 부재로 Error 직접 역직렬화 불가).
    private sealed record ErrorBody(string code);
}
