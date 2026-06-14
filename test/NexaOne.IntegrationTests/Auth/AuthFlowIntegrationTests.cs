using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Auth;

/// <summary>
/// 인증 플로 HTTP 통합 테스트 — AuthController(login/refresh/logout/me)가 SQLite 위에서
/// 인증/인가/직렬화/계약(401 code 포함)을 end-to-end로 지키는지 검증한다.
///
/// 신뢰 가능한 검증(대부분 시드 불필요):
///  - GET  /api/v1/auth/me        : 미인증 401, ADMIN 토큰 200 + userId 포함.
///  - POST /api/v1/auth/login     : 미존재 사용자 → 401 + code "INVALID_CREDENTIALS"(계정 열거 방지).
///  - POST /api/v1/auth/refresh   : 위조 리프레시 토큰 → 401.
///  - POST /api/v1/auth/logout    : [Authorize]라 미인증 401.
///
/// 로그인 라운드트립은 V001__SYS_USER.sql이 시드하는 기본 'admin' 사용자
/// (PASSWORD_HASH = SHA-256("admin") lowercase hex, 레거시 무염 형식)를 사용한다.
/// PasswordHasher.Verify가 레거시 SHA-256 hex를 지원하므로 admin/admin 로그인이 성공하고,
/// 발급 토큰으로 /auth/me가 200을 반환한다(별도 시드/PBKDF2 해시 계산 불필요).
/// 로그인 성공 시 rehash-on-login으로 UserRepository.UpdateAsync(평이한 UPDATE)만 실행되어
/// SQLite에서도 안전하다(아래 suspectedBug의 OUTPUT 절 경로는 타지 않는다).
/// </summary>
public sealed class AuthFlowIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public AuthFlowIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── GET /api/v1/auth/me ─────────────────────────────────────────────────────

    [Fact]
    public async Task Me_requires_auth_and_returns_user_identity_for_admin()
    {
        // 1) 미인증 → 401([Authorize]가 라우팅/핸들러보다 먼저 적용).
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/auth/me")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "[Authorize] GET me는 토큰 없이 401이어야 한다");

        // 2) ADMIN("*") 토큰 → 200 + 토큰의 sub(=userId)가 응답에 그대로 반영되어야 한다.
        //    CreateAuthenticatedClient는 sub="test-admin"으로 토큰을 발급한다.
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/auth/me");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"유효한 ADMIN 토큰이면 me는 200이어야 한다. 응답 본문: {body}");

        var me = await resp.Content.ReadFromJsonAsync<MeResponse>();
        me.Should().NotBeNull("me 응답은 신원 JSON 객체여야 한다");
        me!.UserId.Should().Be("test-admin", "me는 토큰 sub 클레임의 userId를 반환해야 한다");
        me.Roles.Should().Contain("ADMIN", "me는 토큰의 역할 클레임을 반환해야 한다");
    }

    // ── POST /api/v1/auth/login ─────────────────────────────────────────────────

    [Fact]
    public async Task Login_with_unknown_user_returns_401_with_invalid_credentials_code()
    {
        // [AllowAnonymous] 진입점 — 인증 없이 호출 가능. 미존재 사용자는 계정 열거 방지를 위해
        // 동일한 401 + code "INVALID_CREDENTIALS"를 반환해야 한다(시드 불필요: not-found 경로는
        // 실패 이력 INSERT만 수행하며 OUTPUT 절을 타지 않아 SQLite에서 안전하다).
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userId = "no-such-user-xyz",
            password = "whatever",
            plantId = "DEFAULT"
        });
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"미존재 사용자 로그인은 401이어야 한다. 응답 본문: {body}");

        var error = await resp.Content.ReadFromJsonAsync<AuthError>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("INVALID_CREDENTIALS",
            "미존재 사용자는 계정 존재 여부를 드러내지 않는 INVALID_CREDENTIALS code를 반환해야 한다");
    }

    // ── POST /api/v1/auth/refresh ───────────────────────────────────────────────

    [Fact]
    public async Task Refresh_with_forged_token_returns_401()
    {
        // [AllowAnonymous] 진입점 — 위조/만료 리프레시 토큰은 401이어야 한다.
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId = "test-admin",
            refreshToken = "forged-refresh-token-not-issued-by-store"
        });
        var body = await resp.Content.ReadAsStringAsync();

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"발급되지 않은 리프레시 토큰은 401이어야 한다. 응답 본문: {body}");
    }

    // ── POST /api/v1/auth/logout ────────────────────────────────────────────────

    [Fact]
    public async Task Logout_requires_authentication()
    {
        // logout은 [Authorize] — 폐기 대상 userId를 토큰에서만 취하므로 미인증은 401이어야 한다.
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/api/v1/auth/logout", new
        {
            userId = "test-admin",
            refreshToken = "anything"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] logout은 토큰 없이 401이어야 한다(본문 userId를 신뢰하지 않음)");
    }

    // ── 로그인 라운드트립(login → 토큰 → /auth/me) ───────────────────────────────

    [Fact]
    public async Task Login_roundtrip_issues_token_that_authorizes_me()
    {
        // V001 마이그레이션이 시드하는 기본 admin/admin(레거시 SHA-256 hex)로 로그인한다.
        var client = _factory.CreateClient();

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userId = "admin",
            password = "admin",
            plantId = "DEFAULT"
        });
        var loginBody = await loginResp.Content.ReadAsStringAsync();
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"시드된 admin/admin 로그인은 200이어야 한다(레거시 SHA-256 Verify). 응답 본문: {loginBody}");

        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponseDto>();
        login.Should().NotBeNull();
        login!.AccessToken.Should().NotBeNullOrWhiteSpace("로그인 성공은 액세스 토큰을 발급해야 한다");
        login.UserId.Should().Be("admin", "로그인 응답의 userId는 요청 사용자여야 한다");
        login.RequirePasswordChange.Should().BeFalse(
            "기본 admin은 PasswordState=Normal이라 변경 강제 대상이 아니어야 한다");

        // 발급 토큰으로 /auth/me 호출 → 200 + 동일 userId.
        var authed = _factory.CreateClient();
        authed.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", login.AccessToken);

        var meResp = await authed.GetAsync("/api/v1/auth/me");
        var meBody = await meResp.Content.ReadAsStringAsync();
        meResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"로그인으로 발급된 토큰은 me에서 200이어야 한다(발급↔검증 키 일치). 응답 본문: {meBody}");

        var me = await meResp.Content.ReadFromJsonAsync<MeResponse>();
        me.Should().NotBeNull();
        me!.UserId.Should().Be("admin", "로그인 토큰의 sub로 me가 동일 userId를 반환해야 한다");
    }

    [Fact]
    public async Task Login_existing_user_wrong_password_returns_401_not_500()
    {
        // 회귀 — UserRepository.RecordLoginFailureAsync에서 MSSQL 전용 OUTPUT 절을 제거(UPDATE+SELECT 분리).
        // 존재하는 사용자(admin)의 오답 로그인은 실패 카운트를 기록한 뒤 SQLite에서도 500이 아니라 401이어야 한다.
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userId = "admin", password = "definitely-wrong", plantId = "DEFAULT"
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            $"존재 사용자 오답 로그인은 401이어야 한다(실패 이력 기록이 SQLite에서 깨지지 않음). 응답 본문: {body}");
        var error = await resp.Content.ReadFromJsonAsync<AuthError>();
        error!.Code.Should().Be("INVALID_CREDENTIALS", "오답도 계정 존재를 드러내지 않는 동일 code를 유지");
    }

    // 응답 직렬화 계약(JSON camelCase, 대소문자 무시 역직렬화).
    // me: { userId, userName, plantId, roles }
    private sealed record MeResponse(string UserId, string? UserName, string? PlantId, List<string> Roles);

    // login 실패 응답: { code, message }
    private sealed record AuthError(string Code, string Message);

    // LoginResponse 레코드 필드와 정확히 일치(AccessToken/RefreshToken/UserId/UserName/PlantId/Roles/RequirePasswordChange).
    private sealed record LoginResponseDto(
        string AccessToken, string RefreshToken, string UserId, string UserName,
        string PlantId, List<string> Roles, bool RequirePasswordChange);
}
