using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Application.Auth;

namespace NexaOne.IntegrationTests.Auth;

/// <summary>
/// 인증-후 변이(mutation) 플로 HTTP 통합 테스트 — AuthController의 change-password / refresh /
/// forgot-reset-password / me 경로가 SQLite 위에서 인증/인가/직렬화/영속·되읽기를 end-to-end로
/// 지키는지 검증한다. 기존 AuthFlowIntegrationTests(실패/익명/로그인 라운드트립)가 다루지 않은
/// "성공 후 상태 변화"(비밀번호 교체·재로그인, 리프레시 토큰 회전·폐기, 계정 열거 방지 동일 응답)를 덮는다.
///
/// 시드/하니스 전제:
///  - V001 마이그레이션이 admin/admin(레거시 무염 SHA-256 hex, PasswordState=Normal)을 시드한다.
///    PasswordHasher.Verify가 레거시 hex를 검증하므로 admin/admin 로그인이 성공한다.
///  - TestApiFactory는 클래스별 고유 SQLite DB(GUID) + db/migrations 부트스트랩이라
///    이 클래스의 admin 변이는 다른 클래스에 영향을 주지 않는다.
///  - change-password / refresh / me는 폐기/대상 userId를 본문이 아니라 토큰(sub)에서만 취하므로,
///    토큰의 sub를 admin으로 발급해야 admin 행을 대상으로 동작한다. 팩토리 헬퍼는 sub="test-admin"
///    (DB에 없는 행)으로 발급하므로, admin 행을 다뤄야 하는 경로는 아래 AdminClient()로 sub="admin"
///    토큰을 직접 민팅한다(앱의 JwtService 사용 → 발급↔검증 키 일치).
///
/// 테스트 독립성:
///  - 비밀번호를 실제로 교체하는 해피 패스(1개)만 admin 행을 파괴적으로 바꾼다. 다른 admin-의존
///    테스트는 로그인 대신 토큰 직접 민팅으로 admin의 현재 비밀번호 상태와 무관하게 동작한다.
/// </summary>
public sealed class AuthFlowMutationIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public AuthFlowMutationIntegrationTests(TestApiFactory factory) => _factory = factory;

    // 정책 충족 비밀번호(8자+, 대/소문자·숫자·특수문자, admin/Administrator/admin@... 부분문자열 미포함).
    private const string PolicyOkPassword = "NexaOne#2026";

    // ── 토큰 헬퍼 ────────────────────────────────────────────────────────────────

    /// <summary>앱의 JwtService로 sub="admin"(시드 사용자) 토큰을 직접 발급한 클라이언트.
    /// change-password/me가 CurrentUserId()로 admin 행을 대상으로 동작하게 한다.</summary>
    private HttpClient AdminClient(string plantId = "DEFAULT")
    {
        var client = _factory.CreateClient();
        var jwt = _factory.Services.GetRequiredService<IJwtService>();
        var token = jwt.GenerateAccessToken("admin", "Administrator", plantId, new[] { "ADMIN" },
            requirePasswordChange: false, permissions: new[] { "*" });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private HttpClient ClientWithToken(string accessToken)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    // ── POST /api/v1/auth/change-password — 해피 패스 ─────────────────────────────

    [Fact]
    public async Task ChangePassword_happy_path_rotates_password_and_issues_new_token()
    {
        // 1) 시드 admin/admin 로그인 → 발급 토큰의 sub=admin으로 change-password가 admin 행을 대상으로 한다.
        var anon = _factory.CreateClient();
        var loginResp = await anon.PostAsJsonAsync("/api/v1/auth/login", new
        {
            userId = "admin", password = "admin", plantId = "DEFAULT"
        });
        var loginBody = await loginResp.Content.ReadAsStringAsync();
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"시드 admin/admin 로그인은 200이어야 한다. 응답 본문: {loginBody}");
        var login = await loginResp.Content.ReadFromJsonAsync<LoginResponseDto>();
        login.Should().NotBeNull();

        var authed = ClientWithToken(login!.AccessToken);

        // 2) change-password: current=admin / new=정책충족 / confirm 일치 → 200 + 새 access/refresh.
        var changeResp = await authed.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = "admin",
            newPassword = PolicyOkPassword,
            confirmPassword = PolicyOkPassword
        });
        var changeBody = await changeResp.Content.ReadAsStringAsync();
        changeResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"정책 충족 + 현재 비번 일치 + confirm 일치면 change-password는 200이어야 한다. 응답 본문: {changeBody}");

        var changed = await changeResp.Content.ReadFromJsonAsync<TokenPairDto>();
        changed.Should().NotBeNull("change-password 성공은 새 토큰 쌍을 발급해야 한다");
        changed!.AccessToken.Should().NotBeNullOrWhiteSpace("새 access token이 발급되어야 한다");
        changed.RefreshToken.Should().NotBeNullOrWhiteSpace("새 refresh token이 발급되어야 한다");

        // 3) 새 비밀번호로 재로그인 → 200 (비밀번호 교체가 SQLite에 영속·되읽기됨).
        var reloginResp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            userId = "admin", password = PolicyOkPassword, plantId = "DEFAULT"
        });
        var reloginBody = await reloginResp.Content.ReadAsStringAsync();
        reloginResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"변경된 새 비밀번호로 재로그인은 200이어야 한다(영속·되읽기 검증). 응답 본문: {reloginBody}");

        // 4) 구 비밀번호(admin)로 로그인 → 401 (교체로 더 이상 유효하지 않음).
        var oldPwResp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/login", new
        {
            userId = "admin", password = "admin", plantId = "DEFAULT"
        });
        oldPwResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "비밀번호 교체 후 구 비밀번호 로그인은 401이어야 한다");
    }

    // ── POST /api/v1/auth/change-password — 가드 ─────────────────────────────────

    [Fact]
    public async Task ChangePassword_confirm_mismatch_returns_400_before_db_lookup()
    {
        // confirm 불일치는 DB 조회 이전(컨트롤러 첫 가드)에서 400 — DB 행 불필요(test-admin 토큰으로 충분).
        var client = _factory.CreateAuthenticatedClient();   // sub=test-admin, ADMIN
        var resp = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = "whatever",
            newPassword = PolicyOkPassword,
            confirmPassword = PolicyOkPassword + "X"   // 불일치
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"newPassword/confirmPassword 불일치는 400이어야 한다. 응답 본문: {body}");
    }

    [Fact]
    public async Task ChangePassword_policy_violation_returns_400_with_policy_code()
    {
        // 정책 위반은 GetUserAsync 성공 이후 검증되므로 실제 admin 행이 필요하다 → sub=admin 토큰 직접 민팅.
        // (test-admin 토큰이면 GetUserAsync NotFound로 다른 사유의 400이 나온다.)
        var client = AdminClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = "admin",
            newPassword = "weak",       // 8자 미만·복잡도 미달 → 정책 위반
            confirmPassword = "weak"
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"복잡도 미달 새 비밀번호는 400이어야 한다. 응답 본문: {body}");
        var error = await resp.Content.ReadFromJsonAsync<CodeMessageDto>();
        error.Should().NotBeNull();
        error!.Code.Should().Be("PASSWORD_POLICY_VIOLATION",
            "정책 위반 400은 PASSWORD_POLICY_VIOLATION code를 반환해야 한다");
    }

    [Fact]
    public async Task ChangePassword_wrong_current_password_returns_400()
    {
        // current 비밀번호 불일치는 서비스에서 Auth.WrongPassword 실패 → 400.
        // 실제 admin 행을 대상으로 해야 Verify가 동작하므로 sub=admin 토큰을 쓴다.
        var client = AdminClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = "definitely-not-admins-current-password",
            newPassword = PolicyOkPassword,
            confirmPassword = PolicyOkPassword
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"현재 비밀번호 불일치는 400이어야 한다(500 아님). 응답 본문: {body}");
    }

    [Fact]
    public async Task ChangePassword_requires_authentication()
    {
        // [Authorize] — 토큰 없으면 401(본문 신뢰 안 함).
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/auth/change-password", new
        {
            currentPassword = "admin",
            newPassword = PolicyOkPassword,
            confirmPassword = PolicyOkPassword
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] change-password는 토큰 없이 401이어야 한다");
    }

    // ── POST /api/v1/auth/refresh — 성공/회전/폐기 ───────────────────────────────

    [Fact]
    public async Task Refresh_success_rotates_refresh_token_and_revokes_old_one()
    {
        // 유효 refreshToken은 싱글톤 RefreshTokenStore에 등록된 것만 통과한다. 로그인 대신
        // 동일 싱글톤 스토어로 admin에게 직접 발급해 admin 비밀번호 상태와 무관하게 만든다
        // (해피 패스 비번 교체 테스트와의 순서 충돌 제거). 사용자 재평가 게이트는 시드 admin(활성)을 본다.
        var store = _factory.Services.GetRequiredService<IRefreshTokenStore>();
        var issued = await store.IssueAsync("admin");

        // refresh: 발급된 refreshToken → 200 + 새 refreshToken이 이전과 다르다(=회전) + 새 accessToken.
        var refreshResp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId = "admin",
            refreshToken = issued
        });
        var refreshBody = await refreshResp.Content.ReadAsStringAsync();
        refreshResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"유효 refreshToken은 200이어야 한다(RotateAsync + DB 재평가). 응답 본문: {refreshBody}");

        var rotated = await refreshResp.Content.ReadFromJsonAsync<TokenPairDto>();
        rotated.Should().NotBeNull();
        rotated!.AccessToken.Should().NotBeNullOrWhiteSpace("refresh는 새 accessToken을 발급해야 한다");
        rotated.RefreshToken.Should().NotBeNullOrWhiteSpace("refresh는 새 refreshToken을 발급해야 한다");
        rotated.RefreshToken.Should().NotBe(issued,
            "refresh는 refreshToken을 회전(rotate)해 이전과 다른 값을 발급해야 한다");

        // 구 refreshToken 재사용 → 401 (회전 시 폐기됨; 재사용 공격 차단).
        var reuseResp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId = "admin",
            refreshToken = issued
        });
        reuseResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "회전된 구 refreshToken 재사용은 401이어야 한다(폐기 검증)");

        // 회전된 새 refreshToken은 다시 한 번 유효해야 한다(연쇄 회전 가능).
        var chainResp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId = "admin",
            refreshToken = rotated.RefreshToken
        });
        chainResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "회전된 새 refreshToken은 다음 refresh에서 유효해야 한다");
    }

    [Fact]
    public async Task Refresh_after_logout_is_rejected()
    {
        // 싱글톤 스토어로 admin에게 refreshToken 발급 → logout(토큰 sub로 폐기 대상 결정)으로 폐기 →
        // 같은 refreshToken으로 refresh하면 401이어야 한다(폐기 반영).
        var store = _factory.Services.GetRequiredService<IRefreshTokenStore>();
        var issued = await store.IssueAsync("admin");

        // logout은 토큰 sub로 폐기 대상을 정한다 → sub=admin 토큰으로 호출해 admin의 refreshToken을 폐기.
        var adminClient = AdminClient();
        var logoutResp = await adminClient.PostAsJsonAsync("/api/v1/auth/logout", new
        {
            userId = "admin",
            refreshToken = issued
        });
        logoutResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "인증된 logout은 204여야 한다");

        var refreshResp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId = "admin",
            refreshToken = issued
        });
        refreshResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "logout으로 폐기된 refreshToken은 refresh에서 401이어야 한다");
    }

    [Fact]
    public async Task Refresh_for_deactivated_user_is_rejected_even_with_valid_token()
    {
        // refresh는 DB 상태(IsActive/IsDeleted)로 재평가한다(§20.10). 시드 admin은 활성이므로
        // 유효 refreshToken은 통과한다. 비활성 사용자 경로는 별도 HTTP 비활성화 엔드포인트가 없어
        // 직접 재현하기 어렵다 — 대신 "존재하지 않는 사용자"의 refresh는, 설령 동일 userId로
        // 위조 refreshToken을 보내도 항상 401임을 단언해 재평가 게이트가 인증 우회를 막는지 본다.
        var resp = await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            userId = "ghost-user-not-in-db",
            refreshToken = "any-token-even-if-it-were-issued"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "미존재/비활성 사용자의 refresh는 401이어야 한다(DB 재평가 게이트)");
    }

    // ── POST forgot-password / reset-password — 계정 열거 방지(202 동일 응답) ───────

    // 주의: 존재 사용자 + 일치 이메일 경로는 ForceSetPasswordAsync로 admin 비밀번호를 임시값으로
    // 바꾸므로(클래스 내 다른 admin/admin 로그인 테스트와 순서 충돌), 아래 테스트는 admin 비밀번호를
    // 변경하지 않는 경로(미존재 사용자 / 존재-이메일불일치)만 사용해 계정 열거 방지(동일 202)를 단언한다.

    [Fact]
    public async Task ForgotPassword_returns_202_for_unknown_and_email_mismatch_uniformly()
    {
        // §20.10 — 계정 존재 여부·이메일 일치 여부에 관계없이 항상 동일하게 202 Accepted(계정 열거 방지).
        var anon = _factory.CreateClient();

        // (1) 미존재 사용자 → 202 (GetUserAsync 실패로 조용히 종료, 메일/변이 없음).
        var unknownResp = await anon.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            userId = "no-such-user-zzz", email = "ghost@nowhere.invalid"
        });
        var unknownBody = await unknownResp.Content.ReadAsStringAsync();
        unknownResp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            $"미존재 사용자 forgot-password는 202여야 한다(계정 열거 방지). 응답 본문: {unknownBody}");

        // (2) 존재 사용자(admin) + 불일치 이메일 → 동일 202 (이메일 불일치로 조기 종료, 비밀번호 변이 없음).
        var mismatchResp = await anon.PostAsJsonAsync("/api/v1/auth/forgot-password", new
        {
            userId = "admin", email = "wrong-email@elsewhere.invalid"
        });
        var mismatchBody = await mismatchResp.Content.ReadAsStringAsync();
        mismatchResp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            $"존재 사용자 + 이메일 불일치도 동일하게 202여야 한다(존재/일치 여부 비노출). 응답 본문: {mismatchBody}");

        // 두 응답의 상태코드가 동일해야 계정 열거가 불가능하다.
        mismatchResp.StatusCode.Should().Be(unknownResp.StatusCode,
            "미존재 사용자와 존재-이메일불일치 사용자의 응답이 동일해야 계정 열거를 막는다");
    }

    [Fact]
    public async Task ResetPassword_compat_endpoint_returns_202()
    {
        // 구버전 호환 reset-password는 내부적으로 forgot-password와 동일 위임 → 202.
        // admin 비밀번호를 변이시키지 않도록 이메일 불일치 입력을 쓴다(상태코드만 검증).
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/auth/reset-password", new
        {
            userId = "admin", email = "wrong-email@elsewhere.invalid"
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            $"reset-password 호환 엔드포인트는 202여야 한다. 응답 본문: {body}");
    }

    // ── GET /api/v1/auth/me — 권한 토큰의 roles 클레임 반영 ───────────────────────

    [Fact]
    public async Task Me_reflects_roles_claim_for_permission_scoped_token()
    {
        // 권한 제한 토큰(perm:x만 보유)도 roles 클레임(ADMIN)은 그대로 me에 반영되어야 한다.
        // (하니스의 CreateAuthenticatedClient는 권한 제한 시에도 roles=["ADMIN"]로 발급)
        var client = _factory.CreateAuthenticatedClient("fdc:read");
        var resp = await client.GetAsync("/api/v1/auth/me");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"유효 토큰이면 me는 200이어야 한다. 응답 본문: {body}");

        var me = await resp.Content.ReadFromJsonAsync<MeResponse>();
        me.Should().NotBeNull();
        me!.UserId.Should().Be("test-admin", "me는 토큰 sub를 userId로 반환해야 한다");
        me.Roles.Should().Contain("ADMIN", "me는 토큰의 roles 클레임을 반영해야 한다");
    }

    // ── 직렬화 계약(camelCase, 대소문자 무시) ─────────────────────────────────────

    // me: { userId, userName, plantId, roles }
    private sealed record MeResponse(string UserId, string? UserName, string? PlantId, List<string> Roles);

    // LoginResponse 레코드 필드와 정확히 일치.
    private sealed record LoginResponseDto(
        string AccessToken, string RefreshToken, string UserId, string UserName,
        string PlantId, List<string> Roles, bool RequirePasswordChange);

    // change-password / refresh 성공 응답: { accessToken, refreshToken }(익명 객체).
    private sealed record TokenPairDto(string AccessToken, string RefreshToken);

    // 정책 위반 등 code/message 응답.
    private sealed record CodeMessageDto(string Code, string Message);
}
