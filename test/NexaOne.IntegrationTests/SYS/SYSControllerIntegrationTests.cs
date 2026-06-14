using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SYS;

/// <summary>
/// SYS 시스템관리 HTTP 통합 테스트 — V031 마이그레이션으로 생성되는 3개 마스터 테이블
/// (SYS_ROLE / SYS_MENU / SYS_MULTI_LANGUAGE_RESOURCE)이 SQLite에서 실제로 동작하는지
/// (테이블 존재 + 방언 SELECT/INSERT + 인증 + 라우팅 + 직렬화)를 end-to-end로 검증한다.
///
/// V031 추가 전에는 이 세 테이블이 누락되어 roles/menu/languages 엔드포인트의 SELECT가
/// 신규 DB에서 깨졌다 — 아래 읽기 스모크/쓰기 해피패스가 그 갭을 회귀 검증한다.
/// (단, GET menu는 컨트롤러가 예외를 흡수해 빈 배열 200을 반환하므로 SELECT 실패를 직접 회귀하지는
///  못하나, 신규 DB에서 SYS_MENU가 존재할 때 정상 동작함을 스모크로 확인한다.)
///
/// 쓰기 정책(POST roles / POST languages)은 perm:sys:manage를 요구하며, 기본 인증 클라이언트는
/// ADMIN "*"(전체 권한)로 토큰을 발급하므로 정책을 통과한다.
/// </summary>
public sealed class SYSControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public SYSControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── Roles (SYS_ROLE, V031) ──────────────────────────────────────────────────

    [Fact]
    public async Task GetRoles_requires_auth_and_returns_ok()
    {
        // 1) 미인증 → 401([Authorize]가 라우팅보다 먼저 적용).
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/sys/roles")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET roles는 토큰 없이 401이어야 한다");

        // 2) ADMIN → 200 — SYS_ROLE 테이블 존재 + SQLite SELECT + 직렬화 성공(시드 ADMIN 역할이 있으므로 비어있지 않다).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/sys/roles");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SYS_ROLE SELECT가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<RoleDto>>())
            .Should().NotBeNull("roles 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task CreateRole_persists_and_is_retrievable()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:sys:manage 통과
        const string roleId = "SYS-IT-ROLE-1";

        // 1) 역할 등록 — SYS_ROLE INSERT(감사 컬럼 주입 포함). CreateRoleRequest(RoleId, RoleName, Description?)와 정합.
        var createResp = await client.PostAsJsonAsync("/api/v1/sys/roles", new
        {
            roleId,
            roleName = "SYS Test Role",
            description = "integration test role"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"역할 등록(SYS_ROLE INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<RoleDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(roleId, "POST 응답은 등록된 역할(Result.Value)이어야 한다");
        created.RoleName.Should().Be("SYS Test Role");

        // 2) 목록 조회(SYS_ROLE SELECT WHERE IS_DELETED = 0) — 방금 등록한 역할이 영속화됐는지 확인.
        var list = await client.GetFromJsonAsync<List<RoleDto>>("/api/v1/sys/roles");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(r => r.Id == roleId)
            .Which.RoleName.Should().Be("SYS Test Role");

        // 3) 단건 조회(SYS_ROLE SELECT WHERE ROLE_ID) — 라우트 파라미터 바인딩 검증.
        var single = await client.GetFromJsonAsync<RoleDto>($"/api/v1/sys/roles/{roleId}");
        single.Should().NotBeNull();
        single!.Id.Should().Be(roleId);
        single.Description.Should().Be("integration test role");
    }

    // ── Menu (SYS_MENU, V031) ───────────────────────────────────────────────────

    [Fact]
    public async Task GetMenu_requires_auth_and_returns_ok()
    {
        // 1) 미인증 → 401([Authorize]가 라우팅보다 먼저 적용).
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/sys/menu")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET menu는 토큰 없이 401이어야 한다");

        // 2) ADMIN → 200 — SYS_MENU 테이블 존재 + SQLite SELECT(SYS_MENU_ROLE JOIN) + 직렬화 성공.
        //    인증 사용자에게 매핑된 메뉴가 없으면 빈 배열이어도 무방하다(읽기 전용 스모크).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/sys/menu");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GET menu는 SYS_MENU 조회 후 200이어야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<MenuItemDto>>())
            .Should().NotBeNull("menu 응답은 JSON 배열이어야 한다");
    }

    // ── MultiLanguageResource (SYS_MULTI_LANGUAGE_RESOURCE, V031) ────────────────

    [Fact]
    public async Task GetLanguages_requires_auth_and_returns_ok()
    {
        // 1) 미인증 → 401.
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/sys/languages")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET languages는 토큰 없이 401이어야 한다");

        // 2) ADMIN → 200 — SYS_MULTI_LANGUAGE_RESOURCE 테이블 존재 + SQLite SELECT(기본 KoKr) + 직렬화 성공.
        //    필수 쿼리 파라미터 없음(menuId/language 미지정 시 KoKr 기본 조회). 빈 목록도 무방.
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/sys/languages");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SYS_MULTI_LANGUAGE_RESOURCE SELECT(WHERE LANGUAGE)가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<LanguageResourceDto>>())
            .Should().NotBeNull("languages 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task UpsertLanguageResource_persists_and_is_listed_by_menu()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:sys:manage 통과
        const string resourceKey = "SYS.IT.RESOURCE.1";
        const string menuId = "SYS-IT-MENU-1";

        // 1) 언어 리소스 등록 — SYS_MULTI_LANGUAGE_RESOURCE INSERT.
        //    UpsertLanguageResourceRequest(ResourceKey, MenuId, Language, Value)와 정합. LANGUAGE는 enum 이름 문자열.
        var createResp = await client.PostAsJsonAsync("/api/v1/sys/languages", new
        {
            resourceKey,
            menuId,
            language = "KoKr",
            value = "통합 테스트 리소스"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"언어 리소스 등록(SYS_MULTI_LANGUAGE_RESOURCE INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<LanguageResourceDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(resourceKey, "POST 응답은 등록된 리소스(Result.Value)여야 한다");
        created.Value.Should().Be("통합 테스트 리소스");

        // 2) 메뉴별 조회(SYS_MULTI_LANGUAGE_RESOURCE SELECT WHERE MENU_ID) — 영속화 확인.
        var list = await client.GetFromJsonAsync<List<LanguageResourceDto>>(
            $"/api/v1/sys/languages?menuId={menuId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(r => r.Id == resourceKey)
            .Which.MenuId.Should().Be(menuId);
    }

    // 도메인 직렬화(JSON camelCase, JsonStringEnumConverter로 enum은 문자열).
    // Role.Id = ROLE_ID(=PK), MultiLanguageResource.Id = RESOURCE_KEY(=PK), Language는 enum 이름 문자열.
    private sealed record RoleDto(
        string Id, string RoleName, string Description, List<string> Permissions, bool IsDeleted);

    private sealed record MenuItemDto(
        string MenuId, string MenuName, string? ParentMenuId,
        int DisplaySequence, string MenuType, string ProgramId,
        string? ImageId, int Depth, bool IsOrphan);

    private sealed record LanguageResourceDto(
        string Id, string MenuId, string Language, string Value);
}
