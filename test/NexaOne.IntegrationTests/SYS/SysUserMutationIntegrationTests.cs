using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SYS;

/// <summary>
/// SYS 사용자 상태전이(비활성화 / 잠금 해제) HTTP 통합 테스트.
///
/// SysController의 미검증 쓰기/전이 경로를 SQLite에서 end-to-end로 실증한다:
///   - PUT /api/v1/sys/users/{id}/deactivate  → User.Deactivate() (IS_ACTIVE=0, IS_DELETED=1) UPDATE
///   - PUT /api/v1/sys/users/{id}/unlock      → User.Unlock()     (FAIL_COUNT=0, LOCKED_UNTIL=NULL) UPDATE
///
/// 검증 포인트(SQLite 실동작):
///   1) 테이블 존재 — SYS_USER(V001) + 잠금 컬럼(V011: PASSWORD_STATE/FAIL_COUNT/LOCKED_UNTIL).
///      V011은 MSSQL의 다중컬럼 ALTER TABLE ... ADD c1, c2, c3 구문을 쓰며, SQLite는 단일 ADD COLUMN만
///      지원하므로 부트스트랩 변환이 누락/오작동하면 컬럼이 없어 INSERT/SELECT/UPDATE가 깨진다.
///   2) 방언 — UserRepository의 INSERT/UPDATE/SELECT가 SQLite에서 성립.
///      특히 RecordLoginFailureAsync의 CASE WHEN ... 원자 UPDATE(과거 MSSQL OUTPUT 절 제거)가
///      SQLite에서 동작해 5회 실패 시 LOCKED_UNTIL이 실제로 설정되는지 확인한다.
///   3) 인증 — 미인증 401(전이 엔드포인트는 [Authorize] + perm:sys:manage).
///   4) 직렬화 — UserDto(camelCase, PasswordState/Language는 enum 문자열, LockedUntil은 nullable DateTime).
///   5) 영속 — 전이 후 되읽기(GET /users/{id})로 UPDATE가 행에 반영됐는지(상태손실 없음) 확인.
///
/// 시나리오:
///   - 비활성화: 생성(POST /users) → GET 활성 확인 → deactivate → GET 비활성 확인.
///   - 잠금 해제: 생성 → 로그인 5회 실패 누적(POST /auth/login, 잠금 시드) → GET 잠금 확인
///               → unlock → GET 잠금 해제 확인.
///   - 미존재 사용자 전이: 컨트롤러가 Result 실패를 BadRequest로 매핑하므로 400을 단언한다
///     (서비스는 Error.NotFound를 반환 — 의미상 404가 적절하나 컨트롤러가 400으로 평탄화: 제약으로 보고).
///
/// 비-부인성: 전이 후 UPDATED_BY는 토큰 주체(test-admin)로 기록되나 UserDto에 노출되지 않아
/// 되읽기로 직접 단언할 수 없다(컨트롤러는 CurrentUserId를 deactivate/unlock에 전달하지 않고
/// 감사 컬럼은 ServiceObjectProcessor가 주입). 따라서 상태 컬럼 영속으로 전이 성립을 실증한다.
///
/// 클래스별 고유 SQLite 임시파일 DB(GUID) + FK 비강제 + db/migrations 부트스트랩(TestApiFactory).
/// 기본 인증 클라이언트는 ADMIN "*"(전체 권한, sub="test-admin")이라 perm:sys:manage를 통과한다.
/// </summary>
public sealed class SysUserMutationIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public SysUserMutationIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 미인증 401 ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Deactivate_and_unlock_require_auth()
    {
        var anon = _factory.CreateClient();

        // [Authorize]가 라우팅보다 먼저 적용되므로 본문/대상 존재와 무관하게 401.
        (await anon.PutAsync("/api/v1/sys/users/whoever/deactivate", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "deactivate는 토큰 없이 401이어야 한다");

        (await anon.PutAsync("/api/v1/sys/users/whoever/unlock", content: null)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "unlock은 토큰 없이 401이어야 한다");
    }

    // ── 비활성화 해피패스: 생성 → deactivate → 되읽기 ──────────────────────────────

    [Fact]
    public async Task Deactivate_persists_inactive_state_and_is_visible_on_reread()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:sys:manage 통과
        var userId = $"sys-it-deact-{Guid.NewGuid():N}";

        // 1) 사용자 생성 — SYS_USER INSERT(PASSWORD_STATE/FAIL_COUNT/LOCKED_UNTIL 포함).
        //    CreateUserRequest(UserId, UserName, PasswordHash, Email, RoleId, Language="KoKr")와 정합.
        await CreateUserAsync(client, userId);

        // 2) 생성 직후 단건 조회 — IS_ACTIVE = 1 (활성).
        var before = await GetUserAsync(client, userId);
        before.Should().NotBeNull();
        before!.IsActive.Should().BeTrue("생성 직후 사용자는 활성이어야 한다");

        // 3) 비활성화 전이 — IS_ACTIVE=0, IS_DELETED=1, DELETED_AT 설정 UPDATE.
        var deactResp = await client.PutAsync($"/api/v1/sys/users/{userId}/deactivate", content: null);
        var deactBody = await deactResp.Content.ReadAsStringAsync();
        deactResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"deactivate는 SYS_USER UPDATE 성공 시 204여야 한다. 응답 본문: {deactBody}");

        // 4) 되읽기 — UPDATE가 영속화돼 IS_ACTIVE=0이 반영되는지(상태손실 없음) 확인.
        //    GetByIdAsync는 IS_ACTIVE/IS_DELETED 필터가 없어 비활성 사용자도 반환한다(=상태 검증 가능).
        var after = await GetUserAsync(client, userId);
        after.Should().NotBeNull("deactivate 후에도 단건 조회는 사용자를 반환해야 한다(GetById는 활성 필터 없음)");
        after!.IsActive.Should().BeFalse("deactivate UPDATE 후 되읽기에서 IS_ACTIVE=0이 영속돼야 한다");
    }

    // ── 잠금 해제 해피패스: 생성 → 로그인 5회 실패(잠금 시드) → unlock → 되읽기 ──────

    [Fact]
    public async Task Unlock_clears_lock_seeded_by_consecutive_login_failures()
    {
        var client = _factory.CreateAuthenticatedClient();   // 관리자 — 생성/조회/unlock
        var userId = $"sys-it-unlock-{Guid.NewGuid():N}";

        // 1) 사용자 생성. PasswordHash는 검증에 통과하지 않을 임의의 비-PBKDF2 문자열로 둔다 —
        //    아래 로그인 시도는 의도적으로 비밀번호 검증에 실패해 FAIL_COUNT를 누적시킨다.
        await CreateUserAsync(client, userId, passwordHash: "not-a-real-hash");

        // 2) 잠금 시드 — POST /auth/login에 틀린 비밀번호로 MaxConsecutiveFailures(5)회 시도.
        //    UserRepository.RecordLoginFailureAsync의 원자 CASE WHEN UPDATE가 SQLite에서 동작해야
        //    5회째에 LOCKED_UNTIL이 설정된다(과거 MSSQL OUTPUT 절은 SQLite에서 깨졌음 — 회귀 가드).
        //    "auth" 레이트리밋(IP당 10req/min) 한도(5 < 10) 이내라 429를 유발하지 않는다.
        var anon = _factory.CreateClient();
        for (var i = 0; i < 5; i++)
        {
            var loginResp = await anon.PostAsJsonAsync("/api/v1/auth/login", new
            {
                userId,
                password = $"wrong-{i}",
                plantId = "DEFAULT"
            });
            var loginBody = await loginResp.Content.ReadAsStringAsync();
            // 틀린 비밀번호는 401(잠금 전 INVALID_CREDENTIALS, 잠금 후 ACCOUNT_LOCKED). 429/500이면 안 된다.
            loginResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                $"잘못된 로그인 시도 #{i + 1}은 401이어야 한다(레이트리밋/서버오류 아님). 응답 본문: {loginBody}");
        }

        // 3) 잠금 확인 — 되읽기에서 LOCKED_UNTIL이 설정(non-null)됐는지. 여기서 null이면
        //    원자 UPDATE 또는 잠금 컬럼 마이그레이션(V011)이 SQLite에서 동작하지 않는 것이다.
        var locked = await GetUserAsync(client, userId);
        locked.Should().NotBeNull();
        locked!.LockedUntil.Should().NotBeNull(
            "5회 연속 로그인 실패 후 LOCKED_UNTIL이 설정돼야 한다(V011 잠금 컬럼 + 원자 UPDATE의 SQLite 실동작)");

        // 4) 관리자 잠금 해제 전이 — FAIL_COUNT=0, LOCKED_UNTIL=NULL UPDATE.
        var unlockResp = await client.PutAsync($"/api/v1/sys/users/{userId}/unlock", content: null);
        var unlockBody = await unlockResp.Content.ReadAsStringAsync();
        unlockResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"unlock은 SYS_USER UPDATE 성공 시 204여야 한다. 응답 본문: {unlockBody}");

        // 5) 되읽기 — 잠금 해제가 영속화돼 LOCKED_UNTIL=NULL이 반영되는지 확인(상태손실 없음).
        var unlocked = await GetUserAsync(client, userId);
        unlocked.Should().NotBeNull();
        unlocked!.LockedUntil.Should().BeNull(
            "unlock UPDATE 후 되읽기에서 LOCKED_UNTIL=NULL이 영속돼야 한다");
    }

    // ── 미존재 사용자 전이: 컨트롤러의 실패 매핑 검증 ──────────────────────────────

    [Fact]
    public async Task Deactivate_unknown_user_returns_not_found()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PutAsync(
            $"/api/v1/sys/users/no-such-user-{Guid.NewGuid():N}/deactivate", content: null);
        var body = await resp.Content.ReadAsStringAsync();

        // 컨트롤러가 ToActionResult로 Error.Type을 상태에 매핑한다(Wave C-α).
        // DeactivateUserAsync는 미존재 사용자에 Error.NotFound를 돌려주므로 HTTP는 404다.
        // 핵심: 미존재 사용자에 대해 500/예외가 아니라 결정적 4xx로 처리돼야 한다.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"미존재 사용자 deactivate는 NotFound(Error.NotFound 매핑)여야 한다. 응답 본문: {body}");
    }

    [Fact]
    public async Task Unlock_unknown_user_returns_not_found()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PutAsync(
            $"/api/v1/sys/users/no-such-user-{Guid.NewGuid():N}/unlock", content: null);
        var body = await resp.Content.ReadAsStringAsync();

        // UnlockUserAsync도 미존재 사용자에 Error.NotFound를 돌려주므로 HTTP는 404다(Wave C-α).
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"미존재 사용자 unlock은 NotFound(Error.NotFound 매핑)여야 한다. 응답 본문: {body}");
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────────

    private static async Task CreateUserAsync(
        HttpClient client, string userId, string passwordHash = "pw-hash")
    {
        var resp = await client.PostAsJsonAsync("/api/v1/sys/users", new
        {
            userId,
            userName = $"IT User {userId}",
            passwordHash,
            email = $"{userId}@nexaone.local",
            roleId = "ADMIN",         // V031 시드 역할 — 앱 레벨 FK 검증이 있어도 통과.
            language = "KoKr"
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"사용자 생성(SYS_USER INSERT)이 성공해야 한다. 응답 본문: {body}");
    }

    private static async Task<UserDto?> GetUserAsync(HttpClient client, string userId)
    {
        var resp = await client.GetAsync($"/api/v1/sys/users/{userId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"단건 사용자 조회(SYS_USER SELECT)가 성공해야 한다. 응답 본문: {body}");
        return await resp.Content.ReadFromJsonAsync<UserDto>();
    }

    // 컨트롤러의 UserDto와 1:1 (camelCase JSON, enum은 문자열, LockedUntil은 nullable DateTime).
    private sealed record UserDto(
        string Id,
        string UserName,
        string Email,
        string RoleId,
        string Language,
        bool IsActive,
        DateTime? LastLoginAt,
        DateTime? LockedUntil,
        string PasswordState);
}
