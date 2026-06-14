using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Users;

/// <summary>
/// §19.3 — 사용자 등록 신청/승인/반려(UserRegistrationController, Route api/v1/users) HTTP 통합 테스트.
/// 기존 통합 0건 경로를 SQLite에서 end-to-end로 실증한다:
///   - 익명 GET  exists/{userId}      → IsUserIdAvailableAsync (SYS_USER + SYS_USER_REQUEST 조회)
///   - 익명 POST request              → UserApprovalService.RequestAsync → SYS_USER_REQUEST INSERT
///   - 관리자 GET  requests           → SearchAsync (SYS_USER_REQUEST SELECT, perm:sys:user.manage)
///   - 관리자 PATCH requests/{id}/approve → ApproveAsync (SYS_USER INSERT + SYS_USER_REQUEST UPDATE)
///   - 관리자 PATCH requests/{id}/reject  → RejectAsync  (SYS_USER_REQUEST UPDATE)
///
/// 검증 포인트(SQLite 실동작):
///   1) 테이블 존재 — SYS_USER_REQUEST(V015) + SYS_USER(V001)/SYS_ROLE 시드(V031). V015 누락 시
///      exists/request/requests/approve 모두 SELECT/INSERT에서 깨진다(테이블 부재 → 500).
///   2) 방언 — UserRequestRepository의 INSERT/UPDATE/SELECT가 SQLite에서 성립(DATETIME2→TEXT 등 부트스트랩 변환).
///   3) 인증/인가 — 익명 진입점(exists/request)은 [AllowAnonymous] 200; 관리자 경로(requests/approve/reject)는
///      미인증 401 + 권한없는 토큰("fdc:read") 403 + ADMIN("*") 200.
///   4) 직렬화 — UserRequestDto(camelCase, Status는 enum 문자열, nullable DateTime).
///   5) 영속/되읽기 — request → GET requests로 id 회수 → approve/reject 후 상태 전이가 행에 영속되는지 확인.
///
/// 비-부인성: approve/reject는 컨트롤러가 본문이 아닌 토큰 주체(CurrentUserId, sub="test-admin")를
/// approvedBy/rejectedBy로 전달한다. 되읽기에서 ApprovedBy/RejectedBy == "test-admin"을 단언해
/// 승인자/반려자가 토큰으로 기록됨을 검증한다.
///
/// 클래스별 고유 SQLite 임시파일 DB(GUID) + FK 비강제 + db/migrations 부트스트랩(TestApiFactory).
/// 기본 인증 클라이언트는 ADMIN "*"(sub="test-admin")이라 perm:sys:user.manage를 통과한다.
/// 익명 진입점(exists/request)은 "auth" 레이트리밋(IP당 10req/min) 대상 — 이 클래스의 익명 호출
/// 총합(exists 1 + request 3 = 4)은 한도(10) 이내라 429를 유발하지 않는다.
/// </summary>
public sealed class UserRegistrationIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public UserRegistrationIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 익명 GET exists/{userId} — 미존재 ID는 available=true ────────────────────────

    [Fact]
    public async Task CheckUserId_anonymous_returns_available_true_for_unknown_id()
    {
        // [AllowAnonymous] — 토큰 없이 접근 가능. 미존재 ID는 SYS_USER/SYS_USER_REQUEST 어디에도
        // 없으므로 IsUserIdAvailableAsync가 true를 반환한다(테이블 존재 + 두 SELECT의 SQLite 실동작).
        var anon = _factory.CreateClient();
        var userId = $"ureg-it-unknown-{Guid.NewGuid():N}";

        var resp = await anon.GetAsync($"/api/v1/users/exists/{userId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"익명 GET exists는 200이어야 한다(SYS_USER + SYS_USER_REQUEST SELECT 성공). 응답 본문: {body}");

        var dto = await resp.Content.ReadFromJsonAsync<AvailabilityDto>();
        dto.Should().NotBeNull();
        dto!.Available.Should().BeTrue("미존재 ID는 사용 가능(available=true)이어야 한다");
    }

    // ── 익명 POST request — TermsAccepted=true 신청은 200 + Status 'Request' ──────────

    [Fact]
    public async Task SubmitRequest_anonymous_creates_request_in_Request_state()
    {
        var anon = _factory.CreateClient();
        var userId = $"ureg-it-submit-{Guid.NewGuid():N}";

        // [AllowAnonymous] — 약관 동의(TermsAccepted=true) 신청 제출 → SYS_USER_REQUEST INSERT.
        // UserRegistrationRequest(UserId, UserName, Email, Department, Position, PlantId,
        //   DefaultLanguageType?, TermsAccepted, Duty?, CellPhoneNumber?, Address?, Description?, Nickname?)와 정합.
        var resp = await anon.PostAsJsonAsync("/api/v1/users/request", new
        {
            userId,
            userName = "신청 사용자",
            email = $"{userId}@nexaone.local",
            department = "제조기술",
            position = "사원",
            plantId = "DEFAULT",
            defaultLanguageType = "KoKr",
            termsAccepted = true
        });
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"약관 동의 신청은 200이어야 한다(SYS_USER_REQUEST INSERT 성공). 응답 본문: {body}");

        var created = await resp.Content.ReadFromJsonAsync<UserRequestDto>();
        created.Should().NotBeNull();
        created!.UserId.Should().Be(userId, "POST 응답은 등록된 신청(Result.Value)이어야 한다");
        created.Status.Should().Be("Request", "신규 신청의 상태는 'Request'여야 한다");
        created.RequestVersion.Should().Be(1, "신규 신청의 버전은 1이어야 한다");
        created.RequestId.Should().NotBeNullOrWhiteSpace("서버가 REQUEST_ID를 발급해야 한다");

        // 신청 후 exists 는 더 이상 available 이 아니어야 한다(대기 중 신청 존재 → 사용 불가).
        var existsResp = await anon.GetAsync($"/api/v1/users/exists/{userId}");
        existsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var avail = await existsResp.Content.ReadFromJsonAsync<AvailabilityDto>();
        avail!.Available.Should().BeFalse("대기 중 신청이 있는 ID는 사용 불가(available=false)여야 한다");
    }

    // ── 인가: 관리자 GET requests — 미인증 401 / 권한없음 403 / ADMIN 200 ─────────────

    [Fact]
    public async Task GetRequests_enforces_authorization()
    {
        // 1) 미인증 → 401. 컨트롤러 전체 [Authorize(Policy=perm:sys:user.manage)]가 라우팅보다 먼저 적용.
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/users/requests")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET requests는 토큰 없이 401이어야 한다");

        // 2) 인증됐으나 권한없는 토큰("fdc:read") → 403. PermissionRequirement가 sys:user.manage(또는 "*")를
        //    요구하므로 무관 권한 토큰은 거부된다(401 아님 — 인증은 됐고 인가만 실패).
        var weak = _factory.CreateAuthenticatedClient("fdc:read");
        (await weak.GetAsync("/api/v1/users/requests")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "권한없는 토큰의 GET requests는 403이어야 한다");

        // 3) ADMIN("*") → 200. SYS_USER_REQUEST SELECT + 직렬화 성공(빈 목록도 무방).
        var admin = _factory.CreateAuthenticatedClient();
        var resp = await admin.GetAsync("/api/v1/users/requests");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ADMIN GET requests는 200이어야 한다(SearchAsync SELECT 성공). 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<UserRequestDto>>())
            .Should().NotBeNull("requests 응답은 JSON 배열이어야 한다");
    }

    // ── 회귀: userId LIKE 검색 필터가 SQLite에서 동작(과거 MSSQL '%'+@p+'%' 결합 버그) ──────────

    [Fact]
    public async Task GetRequests_userId_like_filter_returns_match_on_sqlite()
    {
        // SearchAsync의 userId 필터는 과거 MSSQL 전용 문자열 결합("%' + @userId + '%")을 써,
        // SQLite에서 '+'가 산술로 평가돼(→0) 필터가 항상 빈 결과였다. 바운드 파라미터 와일드카드
        // (LIKE @userId, 값='%..%')로 교정 후 양방언에서 부분일치가 동작함을 회귀로 가드한다.
        var anon = _factory.CreateClient();
        var admin = _factory.CreateAuthenticatedClient();
        var unique = Guid.NewGuid().ToString("N")[..12];
        var userId = $"ureg-it-like-{unique}";

        (await anon.PostAsJsonAsync("/api/v1/users/request", new
        {
            userId, userName = "검색 대상", email = $"{userId}@nexaone.local",
            department = "제조", position = "사원", plantId = "DEFAULT",
            defaultLanguageType = "KoKr", termsAccepted = true
        })).StatusCode.Should().Be(HttpStatusCode.OK, "검색 회귀 테스트용 신청 제출이 성공해야 한다");

        // userId 부분문자열로 LIKE 필터 → 해당 신청이 조회돼야 한다(필터가 SQLite에서 실제 동작).
        var resp = await admin.GetAsync($"/api/v1/users/requests?userId={unique}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"userId LIKE 필터 조회는 200이어야 한다. 응답 본문: {body}");
        var list = await resp.Content.ReadFromJsonAsync<List<UserRequestDto>>();
        list.Should().NotBeNull();
        list!.Should().Contain(r => r.UserId == userId,
            "userId LIKE 부분일치 필터가 SQLite에서 동작해 신청을 반환해야 한다('+' 결합 버그면 빈 결과로 실패)");

        // 미존재 부분문자열 → 해당 신청 미포함(필터가 실제로 좁혀짐을 반증, 무조건 전체반환 아님).
        var none = await admin.GetFromJsonAsync<List<UserRequestDto>>(
            $"/api/v1/users/requests?userId=zzz-none-{unique}");
        none!.Should().NotContain(r => r.UserId == userId, "일치하지 않는 필터는 해당 신청을 제외해야 한다");
    }

    // ── 승인 플로: request → GET requests로 id 회수 → approve → 상태 'Approved' → 사용자 생성 확인 ──

    [Fact]
    public async Task Approve_flow_creates_user_and_records_approver_from_token()
    {
        var anon = _factory.CreateClient();
        var admin = _factory.CreateAuthenticatedClient();   // "*"(ADMIN, sub="test-admin")
        var userId = $"ureg-it-approve-{Guid.NewGuid():N}";

        // 1) 익명 신청 제출.
        var submit = await anon.PostAsJsonAsync("/api/v1/users/request", new
        {
            userId,
            userName = "승인 대상",
            email = $"{userId}@nexaone.local",
            department = "품질",
            position = "주임",
            plantId = "DEFAULT",
            defaultLanguageType = "KoKr",
            termsAccepted = true
        });
        var submitBody = await submit.Content.ReadAsStringAsync();
        submit.StatusCode.Should().Be(HttpStatusCode.OK, $"신청 제출이 성공해야 한다. 응답 본문: {submitBody}");

        // 2) 관리자 목록 조회(상태 'Request' 필터)에서 방금 신청의 REQUEST_ID 회수.
        //    status 필터는 SearchAsync의 STATUS = @status (동등 비교)로 SQLite에서 안전하게 동작한다.
        var requestId = await ResolveRequestIdAsync(admin, userId);

        // 3) 승인 — ApproveUserRequestRequest(RoleId). 시드 역할 ADMIN을 지정.
        //    컨트롤러가 CurrentUserId(토큰 sub="test-admin")를 approvedBy로 전달 → 비-부인성.
        var approveResp = await admin.PatchAsJsonAsync(
            $"/api/v1/users/requests/{requestId}/approve", new { roleId = "ADMIN" });
        var approveBody = await approveResp.Content.ReadAsStringAsync();
        approveResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"승인은 200이어야 한다(SYS_USER INSERT + SYS_USER_REQUEST UPDATE 성공). 응답 본문: {approveBody}");

        var approved = await approveResp.Content.ReadFromJsonAsync<UserRequestDto>();
        approved.Should().NotBeNull();
        approved!.Status.Should().Be("Approved", "승인 후 상태는 'Approved'여야 한다");
        approved.ApprovedBy.Should().Be("test-admin",
            "승인자는 본문이 아닌 토큰 주체(CurrentUserId='test-admin')로 기록돼야 한다(비-부인성)");
        approved.ApprovedAt.Should().NotBeNull("승인 시각이 기록돼야 한다");

        // 4) 되읽기(GET requests) — 상태 전이가 SYS_USER_REQUEST 행에 영속됐는지(상태손실 없음) 확인.
        var rereadRequest = await ResolveRequestDtoAsync(admin, userId);
        rereadRequest.Status.Should().Be("Approved",
            "approve UPDATE 후 되읽기에서 상태 'Approved'가 영속돼야 한다");
        rereadRequest.ApprovedBy.Should().Be("test-admin",
            "approve UPDATE 후 되읽기에서 승인자(test-admin)가 영속돼야 한다");

        // 5) 승인 시 SYS_USER 행이 생성됐는지 — GET sys/users/{userId}([Authorize]만, ADMIN 통과).
        //    승인은 지정 RoleId(ADMIN)로 사용자를 생성하고 PasswordState=Create(최초 로그인 변경 강제)로 둔다.
        var userResp = await admin.GetAsync($"/api/v1/sys/users/{userId}");
        var userBody = await userResp.Content.ReadAsStringAsync();
        userResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"승인된 신청의 사용자 행이 SYS_USER에 생성돼야 한다(GET sys/users 200). 응답 본문: {userBody}");

        var user = await userResp.Content.ReadFromJsonAsync<UserDto>();
        user.Should().NotBeNull();
        user!.Id.Should().Be(userId, "생성된 사용자의 ID는 신청 ID와 같아야 한다");
        user.RoleId.Should().Be("ADMIN", "승인 시 지정한 RoleId로 사용자가 생성돼야 한다");
        user.Email.Should().Be($"{userId}@nexaone.local", "신청 이메일이 사용자에 전사돼야 한다");
        user.PasswordState.Should().Be("Create",
            "승인으로 생성된 사용자는 최초 로그인 비밀번호 변경 강제 상태(Create)여야 한다");

        // 6) 사용자 생성 후 exists 는 available=false (SYS_USER에 존재).
        var existsResp = await anon.GetAsync($"/api/v1/users/exists/{userId}");
        existsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var avail = await existsResp.Content.ReadFromJsonAsync<AvailabilityDto>();
        avail!.Available.Should().BeFalse("승인으로 SYS_USER에 생성된 ID는 사용 불가여야 한다");
    }

    // ── 반려 플로: request → id 회수 → reject(Reason) → 상태 'Rejected' + 반려자/사유 기록 ──

    [Fact]
    public async Task Reject_flow_marks_rejected_and_records_rejecter_and_reason()
    {
        var anon = _factory.CreateClient();
        var admin = _factory.CreateAuthenticatedClient();   // "*"(ADMIN, sub="test-admin")
        var userId = $"ureg-it-reject-{Guid.NewGuid():N}";

        // 1) 익명 신청 제출.
        var submit = await anon.PostAsJsonAsync("/api/v1/users/request", new
        {
            userId,
            userName = "반려 대상",
            email = $"{userId}@nexaone.local",
            department = "생산",
            position = "기사",
            plantId = "DEFAULT",
            defaultLanguageType = "KoKr",
            termsAccepted = true
        });
        var submitBody = await submit.Content.ReadAsStringAsync();
        submit.StatusCode.Should().Be(HttpStatusCode.OK, $"신청 제출이 성공해야 한다. 응답 본문: {submitBody}");

        var requestId = await ResolveRequestIdAsync(admin, userId);

        // 2) 반려 — RejectUserRequestRequest(Reason). 사유는 필수.
        //    컨트롤러가 CurrentUserId(토큰 sub="test-admin")를 rejectedBy로 전달 → 비-부인성.
        const string reason = "통합 테스트 반려 사유";
        var rejectResp = await admin.PatchAsJsonAsync(
            $"/api/v1/users/requests/{requestId}/reject", new { reason });
        var rejectBody = await rejectResp.Content.ReadAsStringAsync();
        rejectResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"반려는 200이어야 한다(SYS_USER_REQUEST UPDATE 성공). 응답 본문: {rejectBody}");

        var rejected = await rejectResp.Content.ReadFromJsonAsync<UserRequestDto>();
        rejected.Should().NotBeNull();
        rejected!.Status.Should().Be("Rejected", "반려 후 상태는 'Rejected'여야 한다");
        rejected.RejectReason.Should().Be(reason, "반려 사유가 본문에서 기록돼야 한다");
        rejected.RejectedBy.Should().Be("test-admin",
            "반려자는 본문이 아닌 토큰 주체(CurrentUserId='test-admin')로 기록돼야 한다(비-부인성)");
        rejected.RejectedAt.Should().NotBeNull("반려 시각이 기록돼야 한다");

        // 3) 되읽기(GET requests) — 반려 전이가 SYS_USER_REQUEST 행에 영속됐는지 확인.
        var reread = await ResolveRequestDtoAsync(admin, userId);
        reread.Status.Should().Be("Rejected",
            "reject UPDATE 후 되읽기에서 상태 'Rejected'가 영속돼야 한다");
        reread.RejectedBy.Should().Be("test-admin",
            "reject UPDATE 후 되읽기에서 반려자(test-admin)가 영속돼야 한다");
        reread.RejectReason.Should().Be(reason,
            "reject UPDATE 후 되읽기에서 반려 사유가 영속돼야 한다");

        // 4) 반려 후 같은 ID 재신청 가능(§19.3.6) — exists 는 available=true (반려는 미점유로 취급).
        var existsResp = await anon.GetAsync($"/api/v1/users/exists/{userId}");
        existsResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var avail = await existsResp.Content.ReadFromJsonAsync<AvailabilityDto>();
        avail!.Available.Should().BeTrue("반려된 ID는 재신청 가능(available=true)이어야 한다(§19.3.6)");
    }

    // ── 인가: 승인/반려 쓰기 경로 — 미인증 401 / 권한없음 403 ─────────────────────────

    [Fact]
    public async Task Approve_and_reject_require_authorization()
    {
        var anon = _factory.CreateClient();
        var weak = _factory.CreateAuthenticatedClient("fdc:read");
        const string requestId = "no-such-request";

        // 미인증 → 401 (본문/대상 존재와 무관하게 [Authorize]가 먼저 적용).
        (await anon.PatchAsJsonAsync($"/api/v1/users/requests/{requestId}/approve", new { roleId = "ADMIN" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "미인증 approve는 401이어야 한다");
        (await anon.PatchAsJsonAsync($"/api/v1/users/requests/{requestId}/reject", new { reason = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "미인증 reject는 401이어야 한다");

        // 권한없는 토큰 → 403.
        (await weak.PatchAsJsonAsync($"/api/v1/users/requests/{requestId}/approve", new { roleId = "ADMIN" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "권한없는 토큰 approve는 403이어야 한다");
        (await weak.PatchAsJsonAsync($"/api/v1/users/requests/{requestId}/reject", new { reason = "x" }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden, "권한없는 토큰 reject는 403이어야 한다");
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────────

    /// <summary>관리자 GET requests(상태 'Request' 필터)에서 userId에 해당하는 신청 한 건의 REQUEST_ID를 회수.</summary>
    private static async Task<string> ResolveRequestIdAsync(HttpClient admin, string userId)
        => (await ResolveRequestDtoAsync(admin, userId, "Request")).RequestId;

    /// <summary>관리자 GET requests에서 userId에 해당하는 신청 DTO를 회수(없으면 단언 실패).</summary>
    private static async Task<UserRequestDto> ResolveRequestDtoAsync(
        HttpClient admin, string userId, string? status = null)
    {
        // status 동등 필터(SearchAsync STATUS = @status)는 SQLite에서 안전. userId LIKE 필터는
        // 사용하지 않는다(리포가 MSSQL '%'+@p+'%' 문자열 결합을 써 SQLite에서 다르게 평가될 수 있음 —
        // 여기서는 전체/상태 목록을 받아 클라이언트에서 UserId로 정확히 매칭한다).
        var url = status is null ? "/api/v1/users/requests" : $"/api/v1/users/requests?status={status}";
        var resp = await admin.GetAsync(url);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET requests는 200이어야 한다(SearchAsync SELECT 성공). 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<UserRequestDto>>();
        list.Should().NotBeNull("requests 응답은 JSON 배열이어야 한다");
        var match = list!.SingleOrDefault(r => r.UserId == userId);
        match.Should().NotBeNull(
            $"방금 제출한 신청(userId={userId})이 목록에서 조회돼야 한다(INSERT 영속 + SELECT 실동작)");
        return match!;
    }

    // 컨트롤러의 UserRequestDto와 1:1 (camelCase JSON, Status는 enum 문자열, nullable DateTime).
    private sealed record UserRequestDto(
        string RequestId, string UserId, string UserName, string Email,
        string Department, string Position, string? Duty,
        string PlantId, string Language, string? CellPhoneNumber, string? Address,
        string? Description, string? Nickname,
        string Status, int RequestVersion, DateTime RequestedAt, DateTime TermsAcceptedAt,
        string? ApprovedBy, DateTime? ApprovedAt,
        string? RejectReason, string? RejectedBy, DateTime? RejectedAt);

    // GET exists/{userId} 응답: { available: bool } (익명 객체, camelCase).
    private sealed record AvailabilityDto(bool Available);

    // SysController의 UserDto와 1:1 (승인 시 생성된 SYS_USER 행 검증용).
    private sealed record UserDto(
        string Id, string UserName, string Email, string RoleId, string Language,
        bool IsActive, DateTime? LastLoginAt, DateTime? LockedUntil, string PasswordState);
}
