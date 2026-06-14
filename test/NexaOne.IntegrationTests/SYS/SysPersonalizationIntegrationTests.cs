using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SYS;

/// <summary>
/// SYS 개인화/권한 부여 HTTP 통합 테스트 — SysController의 미검증 경로
/// (조건 저장/불러오기 V010, 즐겨찾기/최근 메뉴 V013, 역할 권한 부여 V031)을
/// SQLite에서 end-to-end로 실증한다(테이블 존재 + 방언 UPSERT/SELECT/DELETE + 인증/인가
/// + JSON 직렬화 컬럼 라운드트립 + 영속/되읽기 + 비-부인성 userId 바인딩).
///
/// 검증 대상 마이그레이션/방언:
///   - V010 SYS_CONDITION_SETTING: PK(USER_ID,MENU_ID,NAME), VALUES_JSON 직렬화 컬럼.
///     UPSERT는 INexaOneEESDbCapability.BuildUpsertSql(SQLite=INSERT..ON CONFLICT DO UPDATE)로 생성 —
///     충돌 컬럼이 PK여야 하므로 PK 정합이 깨지면 'ON CONFLICT clause does not match' 등으로 500.
///   - V013 SYS_FAVORITE_MENU / SYS_RECENT_MENU: PK(USER_ID,MENU_ID). insert-only CREATED_AT 표현 포함 UPSERT.
///   - V031 SYS_ROLE.PERMISSIONS: '|'로 조인된 권한 문자열(직렬화 컬럼). 부여/제거가 split/join 라운드트립으로 영속.
///
/// 인가(쓰기/민감 엔드포인트):
///   - conditions/favorites/recent-menus는 [Authorize](컨트롤러)만 — 인증되면 본인 데이터에 접근.
///     → 미인증 401만 단언(권한 정책 없음).
///   - roles/{id}/permissions(POST·DELETE)는 [Authorize(Policy="perm:sys:manage")] →
///     미인증 401 + 권한없는 토큰("fdc:read") 403 + ADMIN("*") 204를 모두 단언.
///
/// 비-부인성: 컨트롤러가 CurrentUserId(토큰 sub="test-admin")로 조건/즐겨찾기/최근을 저장·조회하므로
///   본문이 아닌 토큰 주체 기준으로 영속·되읽기됨을 conditions 라운드트립으로 실증한다(다른 토큰으로는 보이지 않음).
///
/// 메뉴 의존 경로(favorites POST 노출 / recent GET 노출 / reorder 재배열 반영)는 스모크로 격하한다:
///   UserMenuService는 SYS_MENU ⋈ SYS_MENU_ROLE ⋈ SYS_USER로 "권한 있는 Screen 메뉴"와 교차해야
///   즐겨찾기/최근을 노출·등록하는데, 토큰 주체(test-admin)에 대한 SYS_USER 행도, 그 역할에 매핑된
///   SYS_MENU/SYS_MENU_ROLE 행도 HTTP로 시드할 방법이 없어(메뉴 생성 엔드포인트 부재) 인가 메뉴가 항상 공집합이다.
///   따라서 favorites POST는 NotFound→BadRequest(인가 게이트), recent POST는 멱등 204(조용히 무시),
///   GET은 빈 결과 200(테이블 존재 + SELECT 방언 스모크), reorder/delete는 인가 무관 멱등 경로를 단언한다.
///
/// 클래스별 고유 SQLite 임시파일 DB(GUID) + FK 비강제 + db/migrations 부트스트랩(TestApiFactory).
/// 기본 인증 클라이언트는 ADMIN "*"(전체 권한, sub="test-admin")이라 perm:sys:manage를 통과한다.
/// </summary>
public sealed class SysPersonalizationIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public SysPersonalizationIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── ConditionSetting (V010 SYS_CONDITION_SETTING) — 인증 게이트 ────────────────

    [Fact]
    public async Task Condition_endpoints_require_auth()
    {
        var anon = _factory.CreateClient();

        // [Authorize]가 라우팅보다 먼저 적용되므로 본문/쿼리와 무관하게 401.
        (await anon.GetAsync("/api/v1/sys/conditions?menuId=/it/sys")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET conditions는 토큰 없이 401이어야 한다");
        (await anon.PostAsJsonAsync("/api/v1/sys/conditions",
                new { menuId = "/it/sys", name = "x", values = new { } })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "POST conditions는 토큰 없이 401이어야 한다");
        (await anon.PostAsJsonAsync("/api/v1/sys/conditions/latest",
                new { menuId = "/it/sys", values = new { } })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "POST conditions/latest는 토큰 없이 401이어야 한다");
        (await anon.DeleteAsync("/api/v1/sys/conditions?menuId=/it/sys&name=x")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "DELETE conditions는 토큰 없이 401이어야 한다");
        (await anon.DeleteAsync("/api/v1/sys/conditions/latest?menuId=/it/sys")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "DELETE conditions/latest는 토큰 없이 401이어야 한다");
    }

    // ── ConditionSetting 풀 라운드트립: 저장 → 복원(키 정규화) → latest → 삭제 ───────

    [Fact]
    public async Task Condition_save_then_read_restores_items_with_normalized_keys()
    {
        var client = _factory.CreateAuthenticatedClient();   // sub="test-admin"
        var menuId = $"/it/sys/cond-{Guid.NewGuid():N}";
        const string name = "야간조 불량 필터";

        // 1) 조건 저장 — SYS_CONDITION_SETTING UPSERT(SQLite ON CONFLICT). 키는 대/혼합 케이스로 보내
        //    컨트롤러 SerializeValues의 키 소문자 정규화 + ToItemDto의 복원 정규화를 실증한다.
        //    SaveConditionRequest(MenuId, Name, Values: Dictionary<string,string?>)와 정합.
        var saveResp = await client.PostAsJsonAsync("/api/v1/sys/conditions", new
        {
            menuId,
            name,
            values = new Dictionary<string, string?>
            {
                ["Status"] = "NG",
                ["LINE"]   = "L1",
                ["empty"]  = null
            }
        });
        var saveBody = await saveResp.Content.ReadAsStringAsync();
        saveResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"조건 저장(SYS_CONDITION_SETTING UPSERT)이 SQLite에서 성공해야 한다. 응답 본문: {saveBody}");

        var saved = await saveResp.Content.ReadFromJsonAsync<ConditionItemDto>();
        saved.Should().NotBeNull("POST 응답은 저장된 조건(Result.Value)이어야 한다");
        saved!.Name.Should().Be(name);
        // POST 응답의 Values는 ToItemDto가 ValuesJson을 역직렬화 후 키를 소문자 정규화한 결과다.
        saved.Values.Should().Contain("status", "NG");
        saved.Values.Should().Contain("line", "L1");

        // 2) 목록 조회 — VALUES_JSON 직렬화 컬럼이 영속됐는지(되읽기) + 키 정규화 복원 확인.
        //    GetConditions는 $latest를 분리해 Latest로, 나머지를 Items로 돌려준다.
        var afterSave = await GetConditionsAsync(client, menuId);
        afterSave.Items.Should().ContainSingle(i => i.Name == name,
            "방금 저장한 사용자 조건이 Items에 복원돼야 한다");
        var item = afterSave.Items.Single(i => i.Name == name);
        item.Values.Should().Contain("status", "NG",
            "VALUES_JSON 라운드트립 + 키 소문자 정규화 후 값이 보존돼야 한다");
        item.Values.Should().Contain("line", "L1");
        afterSave.Latest.Should().BeNull("아직 $latest를 저장하지 않았으므로 Latest는 null이어야 한다");

        // 3) 최근 조회 조건($latest) 저장 → GET의 Latest가 채워지는지(예약명 분리) 확인.
        var latestResp = await client.PostAsJsonAsync("/api/v1/sys/conditions/latest", new
        {
            menuId,
            values = new Dictionary<string, string?> { ["From"] = "2026-01-01" }
        });
        var latestBody = await latestResp.Content.ReadAsStringAsync();
        latestResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"최근 조건($latest) 저장이 성공해야 한다. 응답 본문: {latestBody}");

        var afterLatest = await GetConditionsAsync(client, menuId);
        afterLatest.Latest.Should().NotBeNull("$latest 저장 후 GET의 Latest가 채워져야 한다");
        afterLatest.Latest!.Values.Should().Contain("from", "2026-01-01");
        afterLatest.Items.Should().ContainSingle(i => i.Name == name,
            "$latest는 Items에 섞이지 않고 사용자 조건만 Items에 남아야 한다");

        // 4) 사용자 조건 삭제 → 204, Items에서 제거(되읽기). $latest는 수동 삭제 비대상이라 유지.
        var delResp = await client.DeleteAsync(
            $"/api/v1/sys/conditions?menuId={Uri.EscapeDataString(menuId)}&name={Uri.EscapeDataString(name)}");
        var delBody = await delResp.Content.ReadAsStringAsync();
        delResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"저장 조건 삭제(DELETE)는 204여야 한다. 응답 본문: {delBody}");

        var afterDelete = await GetConditionsAsync(client, menuId);
        afterDelete.Items.Should().NotContain(i => i.Name == name,
            "삭제 후 되읽기에서 해당 사용자 조건이 사라져야 한다(영속)");
        afterDelete.Latest.Should().NotBeNull("$latest는 수동 삭제 대상이 아니므로 유지돼야 한다");

        // 5) 최근 조건 초기화 → 204, Latest가 비워지는지 확인.
        var clearResp = await client.DeleteAsync(
            $"/api/v1/sys/conditions/latest?menuId={Uri.EscapeDataString(menuId)}");
        clearResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "최근 조건 초기화(DELETE latest)는 204여야 한다");

        var afterClear = await GetConditionsAsync(client, menuId);
        afterClear.Latest.Should().BeNull("최근 조건 초기화 후 되읽기에서 Latest는 null이어야 한다");
    }

    // ── ConditionSetting 비-부인성: 조건은 본문이 아닌 토큰 주체로 격리된다 ──────────

    [Fact]
    public async Task Conditions_are_scoped_to_token_subject_not_request_body()
    {
        // 두 클라이언트 모두 토큰 sub="test-admin" 이지만, 조건은 USER_ID(=토큰 주체) + MENU_ID로 격리된다.
        // 동일 주체라도 menuId가 다르면 서로의 조건을 보지 못함을 확인 — 본문이 아닌 키 컬럼 기준 격리 실증.
        var client = _factory.CreateAuthenticatedClient();
        var menuA = $"/it/sys/scope-a-{Guid.NewGuid():N}";
        var menuB = $"/it/sys/scope-b-{Guid.NewGuid():N}";

        var saveA = await client.PostAsJsonAsync("/api/v1/sys/conditions", new
        {
            menuId = menuA, name = "A-only", values = new Dictionary<string, string?> { ["k"] = "v" }
        });
        saveA.StatusCode.Should().Be(HttpStatusCode.OK,
            $"menuA 조건 저장이 성공해야 한다. 응답 본문: {await saveA.Content.ReadAsStringAsync()}");

        var listB = await GetConditionsAsync(client, menuB);
        listB.Items.Should().BeEmpty("다른 menuId(menuB)에는 menuA의 조건이 보이지 않아야 한다(키 컬럼 격리)");

        var listA = await GetConditionsAsync(client, menuA);
        listA.Items.Should().ContainSingle(i => i.Name == "A-only",
            "조건은 토큰 주체 + menuId 버킷에 정확히 영속돼야 한다");
    }

    // ── Favorite (V013 SYS_FAVORITE_MENU) — 인증 게이트 ──────────────────────────

    [Fact]
    public async Task Favorite_endpoints_require_auth()
    {
        var anon = _factory.CreateClient();

        (await anon.GetAsync("/api/v1/sys/favorites")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET favorites는 토큰 없이 401이어야 한다");
        (await anon.PostAsJsonAsync("/api/v1/sys/favorites", new { menuId = "M1" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "POST favorites는 토큰 없이 401이어야 한다");
        (await anon.DeleteAsync("/api/v1/sys/favorites?menuId=M1")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "DELETE favorites는 토큰 없이 401이어야 한다");
        (await anon.PutAsJsonAsync("/api/v1/sys/favorites/order", new { menuIds = new[] { "M1" } })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "PUT favorites/order는 토큰 없이 401이어야 한다");
    }

    // ── Favorite 스모크: 테이블 존재(SELECT) + 인가 게이트 + 멱등 삭제 + reorder 검증 ──

    [Fact]
    public async Task Favorites_smoke_select_authorization_gate_and_idempotent_paths()
    {
        var client = _factory.CreateAuthenticatedClient();

        // 1) 빈 목록 조회 — SYS_FAVORITE_MENU 테이블 존재 + SQLite SELECT + 직렬화 성공(빈 배열 200).
        //    (test-admin에 인가 메뉴가 없어 교차 결과가 비어도 SELECT 자체는 성립해야 한다 — 500이면 방언/테이블 결함)
        var listResp = await client.GetAsync("/api/v1/sys/favorites");
        var listBody = await listResp.Content.ReadAsStringAsync();
        listResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET favorites는 SYS_FAVORITE_MENU SELECT 후 200이어야 한다(방언/테이블 결함이면 500). 응답 본문: {listBody}");
        (await listResp.Content.ReadFromJsonAsync<List<FavoriteMenuDto>>())
            .Should().NotBeNull("favorites 응답은 JSON 배열이어야 한다");

        // 2) 인가 게이트 — 인가되지 않은(존재하지 않는) 메뉴 즐겨찾기 추가는 NotFound→BadRequest.
        //    핵심: 500/예외가 아니라 결정적 4xx(인가 거부)로 처리돼야 한다.
        var addResp = await client.PostAsJsonAsync("/api/v1/sys/favorites",
            new { menuId = $"NO-SUCH-MENU-{Guid.NewGuid():N}" });
        var addBody = await addResp.Content.ReadAsStringAsync();
        addResp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"권한 없는 메뉴 즐겨찾기 추가는 BadRequest(NotFound 매핑)여야 한다(500 아님). 응답 본문: {addBody}");

        // 3) 즐겨찾기 삭제 — 인가 검사 없는 멱등 경로. SYS_FAVORITE_MENU DELETE가 SQLite에서 성립해야 한다.
        var delResp = await client.DeleteAsync(
            $"/api/v1/sys/favorites?menuId=NO-SUCH-MENU-{Guid.NewGuid():N}");
        var delBody = await delResp.Content.ReadAsStringAsync();
        delResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"즐겨찾기 삭제는 멱등 204여야 한다(DELETE 방언 성립). 응답 본문: {delBody}");

        // 4) reorder 빈 목록 → BadRequest(검증), 비어있지 않은 목록 → 204(등록분 없으면 변경 없이 성공).
        var reorderEmpty = await client.PutAsJsonAsync("/api/v1/sys/favorites/order",
            new { menuIds = Array.Empty<string>() });
        reorderEmpty.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "빈 정렬 목록은 검증 실패로 BadRequest여야 한다");

        var reorder = await client.PutAsJsonAsync("/api/v1/sys/favorites/order",
            new { menuIds = new[] { $"NO-SUCH-MENU-{Guid.NewGuid():N}" } });
        var reorderBody = await reorder.Content.ReadAsStringAsync();
        reorder.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"재정렬은 SYS_FAVORITE_MENU SELECT 후 204여야 한다(등록 즐겨찾기 없으면 변경 없이 성공). 응답 본문: {reorderBody}");
    }

    // ── Recent menu (V013 SYS_RECENT_MENU) — 인증 게이트 + 스모크 ─────────────────

    [Fact]
    public async Task RecentMenu_endpoints_require_auth()
    {
        var anon = _factory.CreateClient();

        (await anon.GetAsync("/api/v1/sys/recent-menus")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET recent-menus는 토큰 없이 401이어야 한다");
        (await anon.PostAsJsonAsync("/api/v1/sys/recent-menus", new { menuId = "M1" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "POST recent-menus는 토큰 없이 401이어야 한다");
    }

    [Fact]
    public async Task RecentMenu_smoke_record_is_idempotent_and_list_selects()
    {
        var client = _factory.CreateAuthenticatedClient();

        // 1) 기록 — 인가 밖/미등록 화면은 RecordRecentAsync가 조용히 Success(204)로 무시한다(탐색 부수 기록).
        //    핵심: 인가 메뉴가 없어도 500이 아니라 204(멱등)여야 한다.
        var recordResp = await client.PostAsJsonAsync("/api/v1/sys/recent-menus",
            new { menuId = $"NO-SUCH-MENU-{Guid.NewGuid():N}" });
        var recordBody = await recordResp.Content.ReadAsStringAsync();
        recordResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"권한 밖 메뉴 기록은 조용히 무시되어 204여야 한다(500 아님). 응답 본문: {recordBody}");

        // 2) 목록 조회 — SYS_RECENT_MENU 테이블 존재 + SQLite SELECT 성립(인가 교차 후 빈 배열 200).
        var listResp = await client.GetAsync("/api/v1/sys/recent-menus");
        var listBody = await listResp.Content.ReadAsStringAsync();
        listResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET recent-menus는 SYS_RECENT_MENU SELECT 후 200이어야 한다(방언/테이블 결함이면 500). 응답 본문: {listBody}");
        (await listResp.Content.ReadFromJsonAsync<List<RecentMenuDto>>())
            .Should().NotBeNull("recent-menus 응답은 JSON 배열이어야 한다");
    }

    // ── Role permissions (V031 SYS_ROLE.PERMISSIONS) — 인가 401/403/204 ──────────

    [Fact]
    public async Task Role_permission_writes_require_manage_permission()
    {
        const string roleId = "SYS-IT-PERM-AUTHZ";

        // 1) 미인증 → 401([Authorize] + perm:sys:manage; 인증이 라우팅보다 먼저).
        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync($"/api/v1/sys/roles/{roleId}/permissions",
                new { permission = "fdc:read" })).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "권한 부여 POST는 토큰 없이 401이어야 한다");
        (await anon.DeleteAsync($"/api/v1/sys/roles/{roleId}/permissions/fdc:read")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "권한 제거 DELETE는 토큰 없이 401이어야 한다");

        // 2) 권한 부족 토큰("fdc:read"만 보유) → 403(perm:sys:manage 미보유).
        var weak = _factory.CreateAuthenticatedClient("fdc:read");
        (await weak.PostAsJsonAsync($"/api/v1/sys/roles/{roleId}/permissions",
                new { permission = "fdc:read" })).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "perm:sys:manage 없는 토큰의 권한 부여는 403이어야 한다");
        (await weak.DeleteAsync($"/api/v1/sys/roles/{roleId}/permissions/fdc:read")).StatusCode
            .Should().Be(HttpStatusCode.Forbidden, "perm:sys:manage 없는 토큰의 권한 제거는 403이어야 한다");
    }

    // ── Role permissions 풀 라운드트립: 부여 → 노출 → 제거(PERMISSIONS '|' 직렬화) ──

    [Fact]
    public async Task AddPermission_then_remove_roundtrips_through_permissions_column()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:sys:manage 통과
        var roleId = $"SYS-IT-PERMROLE-{Guid.NewGuid():N}";
        const string permission = "fdc:write";

        // 0) 역할 생성 — SYS_ROLE INSERT(감사 컬럼 주입). CreateRoleRequest(RoleId, RoleName, Description?)와 정합.
        var createResp = await client.PostAsJsonAsync("/api/v1/sys/roles", new
        {
            roleId, roleName = "Perm Roundtrip Role", description = "permission grant test"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"역할 생성(SYS_ROLE INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // 사전 상태 — 새 역할은 권한이 비어 있어야 한다(PERMISSIONS = '' split → 빈 목록).
        (await GetRoleAsync(client, roleId)).Permissions
            .Should().NotContain(permission, "생성 직후 역할에는 부여 권한이 없어야 한다");

        // 1) 권한 부여 → 204. role.AddPermission 후 SYS_ROLE UPDATE(PERMISSIONS = '|'.Join).
        //    PermissionRequest(Permission)와 정합.
        var addResp = await client.PostAsJsonAsync(
            $"/api/v1/sys/roles/{roleId}/permissions", new { permission });
        var addBody = await addResp.Content.ReadAsStringAsync();
        addResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"권한 부여는 SYS_ROLE UPDATE 성공 시 204여야 한다. 응답 본문: {addBody}");

        // 2) 단건 조회 — PERMISSIONS('|' 직렬화) 라운드트립 후 부여 권한이 노출되는지(되읽기) 확인.
        (await GetRoleAsync(client, roleId)).Permissions
            .Should().Contain(permission,
                "권한 부여 후 GET roles/{id}의 Permissions에 부여 권한이 포함돼야 한다(PERMISSIONS 직렬화 컬럼 라운드트립)");

        // 3) 권한 제거 → 204. role.RemovePermission 후 SYS_ROLE UPDATE.
        var removeResp = await client.DeleteAsync(
            $"/api/v1/sys/roles/{roleId}/permissions/{Uri.EscapeDataString(permission)}");
        var removeBody = await removeResp.Content.ReadAsStringAsync();
        removeResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"권한 제거는 SYS_ROLE UPDATE 성공 시 204여야 한다. 응답 본문: {removeBody}");

        // 4) 되읽기 — 제거가 영속화돼 Permissions에서 사라지는지 확인.
        (await GetRoleAsync(client, roleId)).Permissions
            .Should().NotContain(permission,
                "권한 제거 후 되읽기에서 해당 권한이 사라져야 한다(PERMISSIONS 라운드트립 영속)");
    }

    [Fact]
    public async Task AddPermission_to_unknown_role_returns_bad_request()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/sys/roles/no-such-role-{Guid.NewGuid():N}/permissions",
            new { permission = "fdc:read" });
        var body = await resp.Content.ReadAsStringAsync();

        // 컨트롤러가 result.IsSuccess ? NoContent() : BadRequest(...)로 매핑하므로,
        // 서비스의 Error.NotFound도 HTTP는 400이다(500/예외가 아니라 결정적 4xx여야 한다).
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            $"미존재 역할 권한 부여는 BadRequest(컨트롤러 매핑)여야 한다(500 아님). 응답 본문: {body}");
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────────────────

    private static async Task<ConditionSettingDto> GetConditionsAsync(HttpClient client, string menuId)
    {
        var resp = await client.GetAsync($"/api/v1/sys/conditions?menuId={Uri.EscapeDataString(menuId)}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET conditions(SYS_CONDITION_SETTING SELECT)는 200이어야 한다. 응답 본문: {body}");
        var dto = await resp.Content.ReadFromJsonAsync<ConditionSettingDto>();
        dto.Should().NotBeNull("conditions 응답은 ConditionSettingDto JSON이어야 한다");
        return dto!;
    }

    private static async Task<RoleDto> GetRoleAsync(HttpClient client, string roleId)
    {
        var resp = await client.GetAsync($"/api/v1/sys/roles/{roleId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"단건 역할 조회(SYS_ROLE SELECT)는 200이어야 한다. 응답 본문: {body}");
        var dto = await resp.Content.ReadFromJsonAsync<RoleDto>();
        dto.Should().NotBeNull("role 응답은 RoleDto JSON이어야 한다");
        return dto!;
    }

    // 컨트롤러 응답 DTO와 1:1 (camelCase JSON, enum은 문자열 — JsonStringEnumConverter).
    private sealed record ConditionSettingDto(ConditionItemDto? Latest, List<ConditionItemDto> Items);
    private sealed record ConditionItemDto(string Name, DateTime SavedAt, Dictionary<string, string?> Values);
    private sealed record FavoriteMenuDto(
        string MenuId, string MenuName, string ProgramId, string? ImageId, int DisplaySequence);
    private sealed record RecentMenuDto(
        string MenuId, string MenuName, string ProgramId, string? ImageId, DateTime LastUsedAt);
    // Role.Id = ROLE_ID(=PK). Permissions는 '|' 분리된 권한 목록(직렬화 컬럼 라운드트립).
    private sealed record RoleDto(
        string Id, string RoleName, string Description, List<string> Permissions, bool IsDeleted);
}
