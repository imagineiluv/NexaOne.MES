using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using NexaOne.Application.Auth;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>§20.10 — pwdChange 강제 변경 게이트(PasswordChangeRequiredMiddleware) 이식 검증.
/// (a) 단위 path-matrix: DefaultHttpContext로 미들웨어 직접 호출 — 차단(업무 /api/v1/* 비-auth + /hubs)/허용(auth·정적·진단).
/// (b) E2E 수명주기: 강제변경 사용자(register PASSWORD_STATE='Create') 토큰이 업무 API에서 403,
/// auth는 허용, change-password 성공 후 새 토큰은 게이트 해제됨을 통합 호스트 + SQLite(전용 DB)로 입증.</summary>
public static class PasswordChangeGateTests
{
    // ── (a) 단위 path-matrix — WebApplicationFactory 불필요 ───────────────────────────
    public sealed class Unit
    {
        private static DefaultHttpContext Context(string path, bool pwdChange)
        {
            var ctx = new DefaultHttpContext();
            ctx.Request.Path = path;
            ctx.Response.Body = new MemoryStream();
            if (pwdChange)
                ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                    new[] { new Claim(JwtService.PasswordChangeClaim, "true") }, "test"));
            return ctx;
        }

        [Theory]
        [InlineData("/api/v1/query/MDM.PlantList")]
        [InlineData("/api/v1/sys/queries")]
        [InlineData("/api/v1/est/states")]
        [InlineData("/hubs/smartees")]
        public async Task Business_paths_are_blocked_with_403(string path)
        {
            var nextCalled = false;
            var mw = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            var ctx = Context(path, pwdChange: true);

            await mw.InvokeAsync(ctx);

            nextCalled.Should().BeFalse("차단된 요청은 다음 미들웨어로 가면 안 된다");
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
            ctx.Response.Body.Position = 0;
            var body = await new StreamReader(ctx.Response.Body).ReadToEndAsync();
            body.Should().Contain("PASSWORD_CHANGE_REQUIRED");
        }

        [Theory]
        [InlineData("/api/v1/auth/change-password")]
        [InlineData("/api/v1/auth/logout")]
        [InlineData("/api/v1/auth/me")]
        [InlineData("/health")]
        [InlineData("/diag")]
        [InlineData("/spa/index.html")]
        [InlineData("/meta/DEMO_GRID")]
        public async Task Auth_ui_and_diagnostic_paths_are_allowed(string path)
        {
            var nextCalled = false;
            var mw = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            var ctx = Context(path, pwdChange: true);

            await mw.InvokeAsync(ctx);

            nextCalled.Should().BeTrue("auth·정적 셸·진단 경로까지 막으면 강제변경 사용자가 비번을 바꿀 방법이 없다");
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }

        [Fact]
        public async Task User_without_claim_passes_through()
        {
            var nextCalled = false;
            var mw = new PasswordChangeRequiredMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            var ctx = Context("/api/v1/query/MDM.PlantList", pwdChange: false);

            await mw.InvokeAsync(ctx);

            nextCalled.Should().BeTrue("클레임 없는 사용자는 통과해야 한다");
            ctx.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        }
    }

    // ── (b) E2E 수명주기 — GatewayAuthCompletenessTests 팩토리 복제(전용 DB, modules-OFF, admin 시드) ──
    private const string Secret = "pwdchange-gate-e2e-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-pwdchange-gate-test";

    public sealed class GateFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-pwdchange-gate-{Guid.NewGuid():N}.db");
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

    private static void SetBearer(HttpClient client, string accessToken) =>
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

    public sealed class Lifecycle : IClassFixture<GateFactory>
    {
        private readonly GateFactory _factory;
        public Lifecycle(GateFactory factory) => _factory = factory;

        [Fact]
        public async Task ForcedChange_user_is_blocked_then_change_password_lifts_the_gate()
        {
            // 1) admin/admin 로그인 → mc1 등록(PASSWORD_STATE='Create').
            var admin = _factory.CreateClient();
            var adminLogin = await admin.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "admin", password = "admin", plantId = "DEFAULT" });
            adminLogin.StatusCode.Should().Be(HttpStatusCode.OK, "admin 부트스트랩 시드로 로그인 성공해야 한다");
            var adminBody = await adminLogin.Content.ReadFromJsonAsync<LoginBody>();
            adminBody.Should().NotBeNull();
            SetBearer(admin, adminBody!.accessToken);

            var reg = await admin.PostAsJsonAsync("/api/v1/auth/register",
                new { userId = "mc1", userName = "MC1", password = "McUser!99", email = "mc@x.com", roleId = "OPERATOR" });
            reg.StatusCode.Should().Be(HttpStatusCode.OK, "admin 권한으로 mc1 등록은 200이어야 한다");

            // 2) mc1 로그인 → requirePasswordChange==true. 그 토큰으로 업무 API 차단·auth 허용.
            var mc1 = _factory.CreateClient();
            var mc1Login = await mc1.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "mc1", password = "McUser!99", plantId = "DEFAULT" });
            mc1Login.StatusCode.Should().Be(HttpStatusCode.OK, "등록한 mc1로 로그인 성공해야 한다");
            var mc1Body = await mc1Login.Content.ReadFromJsonAsync<LoginBody>();
            mc1Body.Should().NotBeNull();
            mc1Body!.requirePasswordChange.Should().BeTrue("신규 등록 사용자는 PASSWORD_STATE='Create'로 최초 변경 강제");

            SetBearer(mc1, mc1Body.accessToken);

            // 업무 API → 403 + 본문 PASSWORD_CHANGE_REQUIRED (게이트 차단 실측).
            var blocked = await mc1.PostAsJsonAsync("/api/v1/query/MDM.PlantList", new { });
            blocked.StatusCode.Should().Be(HttpStatusCode.Forbidden, "강제변경 사용자의 업무 API는 403이어야 한다");
            var blockedBody = await blocked.Content.ReadAsStringAsync();
            blockedBody.Should().Contain("PASSWORD_CHANGE_REQUIRED", "차단 본문은 PASSWORD_CHANGE_REQUIRED 코드여야 한다");

            // auth(me)는 허용 → 200.
            var me = await mc1.GetAsync("/api/v1/auth/me");
            me.StatusCode.Should().Be(HttpStatusCode.OK, "auth 경로는 강제변경 중에도 허용되어야 한다");

            // 3) change-password → 200(클레임 없는 새 토큰).
            var change = await mc1.PostAsJsonAsync("/api/v1/auth/change-password",
                new { currentPassword = "McUser!99", newPassword = "McNew!Pass1", confirmPassword = "McNew!Pass1" });
            change.StatusCode.Should().Be(HttpStatusCode.OK, "유효한 변경은 200(새 토큰)이어야 한다");
            var tokens = await change.Content.ReadFromJsonAsync<RefreshBody>();
            tokens.Should().NotBeNull();
            tokens!.accessToken.Should().NotBeNullOrEmpty();

            // 4) 새 토큰으로 업무 API → 403 아님(게이트 해제 실측). 모듈 OFF라 데이터상 200/빈배열일 수 있으나
            //    핵심은 PASSWORD_CHANGE_REQUIRED가 아니라는 것.
            var fresh = _factory.CreateClient();
            SetBearer(fresh, tokens.accessToken);
            var lifted = await fresh.PostAsJsonAsync("/api/v1/query/MDM.PlantList", new { });
            var liftedBody = await lifted.Content.ReadAsStringAsync();
            (lifted.StatusCode != HttpStatusCode.Forbidden || !liftedBody.Contains("PASSWORD_CHANGE_REQUIRED"))
                .Should().BeTrue("변경 성공 후 새 토큰은 게이트가 해제되어 PASSWORD_CHANGE_REQUIRED로 차단되면 안 된다");
        }
    }

    // ── 응답 형태(camelCase 기본 직렬화) ──────────────────────────────────────────────
    private sealed record LoginBody(string accessToken, string refreshToken, string userId, string userName,
        string plantId, List<string> roles, bool requirePasswordChange);
    private sealed record RefreshBody(string accessToken, string refreshToken);
}
