using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Screens;

/// <summary>
/// ScreenDefinitionController(api/v1/sys/screen-definitions) HTTP 통합 테스트 — 통합 0건이던 경로를
/// SQLite에서 end-to-end로 검증한다(테이블 존재 + 방언 + 인증/인가 + 직렬화 + 영속/되읽기).
///
/// 검증 대상(읽은 사실):
///  - 마이그레이션 V024__SYS_SCREEN_DEFINITION.sql 이 SYS_SCREEN_DEFINITION(UI_ID PK, TITLE,
///    DEFINITION_JSON, 감사 컬럼)을 생성한다. NVARCHAR(MAX)→TEXT, DATETIME2→TEXT,
///    GETUTCDATE()→CURRENT_TIMESTAMP 로 SQLite 부트스트랩이 변환한다.
///  - DI(ServiceCollectionExtensions)는 IScreenDefinitionStore → DB 백엔드 ScreenDefinitionStore 를
///    등록한다(In-Memory 가 아님). 읽기는 QueryRepository SELECT, 쓰기는 방언 UPSERT(SQLite ON CONFLICT).
///  - 컨트롤러: GET list / GET {uiId} 는 [Authorize](인증), PUT {uiId} 는 perm:sys:manage(관리 권한),
///    Title/DefinitionJson 공백이면 400, 미존재 GET {uiId} 는 404. PUT 성공은 Ok()(200).
///  - 도메인 직렬화는 camelCase(UiId→uiId, Title→title, DefinitionJson→definitionJson).
///
/// 인가: 미인증 401(GET·PUT), 권한 없는 토큰("fdc:read") PUT 403, ADMIN("*") PUT 200.
/// </summary>
public sealed class ScreenDefinitionIntegrationTests : IClassFixture<TestApiFactory>
{
    private const string BaseRoute = "/api/v1/sys/screen-definitions";

    private readonly TestApiFactory _factory;

    public ScreenDefinitionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 인증 (GET list / GET {uiId} 는 [Authorize]) ──────────────────────────────

    [Fact]
    public async Task GetList_without_token_is_unauthorized()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync(BaseRoute);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "GET list 는 토큰 없이 401이어야 한다([Authorize]가 라우팅보다 먼저 적용)");
    }

    [Fact]
    public async Task GetById_without_token_is_unauthorized()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.GetAsync($"{BaseRoute}/SCR-UNAUTH");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "GET {uiId} 는 토큰 없이 401이어야 한다");
    }

    // ── 인가 (PUT {uiId} 는 perm:sys:manage) ─────────────────────────────────────

    [Fact]
    public async Task Put_without_token_is_unauthorized()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsJsonAsync($"{BaseRoute}/SCR-UNAUTH-PUT", new
        {
            title = "Anon",
            definitionJson = "{}"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "PUT upsert 는 토큰 없이 401이어야 한다(인가가 핸들러보다 우선)");
    }

    [Fact]
    public async Task Put_with_insufficient_permission_is_forbidden()
    {
        // perm:sys:manage 가 아닌 권한만 보유한 인증 토큰.
        var client = _factory.CreateAuthenticatedClient("fdc:read");
        var resp = await client.PutAsJsonAsync($"{BaseRoute}/SCR-FORBIDDEN", new
        {
            title = "Forbidden",
            definitionJson = "{}"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "perm:sys:manage 권한이 없으면 PUT upsert 는 403이어야 한다(인증만으로 쓰기 불가)");
    }

    // ── 영속/되읽기 해피패스 (upsert → GET {uiId} → GET list) ────────────────────

    [Fact]
    public async Task Upsert_then_get_by_id_returns_same_json_and_is_listed()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:sys:manage 통과
        const string uiId = "SCR-IT-1";
        const string title = "통합 테스트 화면";
        const string definitionJson = "{\"fields\":[{\"name\":\"lotId\",\"type\":\"text\"}],\"layout\":\"grid\"}";

        // 1) PUT upsert — SYS_SCREEN_DEFINITION 방언 UPSERT(SQLite ON CONFLICT)가 성공해야 한다.
        var putResp = await client.PutAsJsonAsync($"{BaseRoute}/{uiId}", new { title, definitionJson });
        var putBody = await putResp.Content.ReadAsStringAsync();
        putResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"PUT upsert(INSERT ... ON CONFLICT)가 SQLite에서 성공해야 한다. 응답 본문: {putBody}");

        // 2) GET {uiId} — SELECT(WHERE UI_ID)로 동일 Title/DefinitionJson 이 되읽혀야 한다(영속화 + 직렬화).
        var getResp = await client.GetAsync($"{BaseRoute}/{uiId}");
        var getBody = await getResp.Content.ReadAsStringAsync();
        getResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"등록한 화면 정의는 GET {{uiId}}로 200이어야 한다. 응답 본문: {getBody}");

        var fetched = await getResp.Content.ReadFromJsonAsync<ScreenDefinitionDto>();
        fetched.Should().NotBeNull("GET {uiId} 응답은 화면 정의 객체여야 한다");
        fetched!.UiId.Should().Be(uiId);
        fetched.Title.Should().Be(title, "Title 이 그대로 되읽혀야 한다");
        fetched.DefinitionJson.Should().Be(definitionJson,
            "DefinitionJson 은 백엔드가 불투명 저장하므로 입력 그대로 되읽혀야 한다");

        // 3) GET list — 목록(SELECT ORDER BY UI_ID)에 방금 등록한 정의가 포함돼야 한다.
        var list = await client.GetFromJsonAsync<List<ScreenDefinitionDto>>(BaseRoute);
        list.Should().NotBeNull("list 응답은 JSON 배열이어야 한다");
        list!.Should().ContainSingle(d => d.UiId == uiId)
            .Which.Title.Should().Be(title);
    }

    // ── upsert 갱신 (동일 uiId 재 PUT) ───────────────────────────────────────────

    [Fact]
    public async Task Upsert_same_uiId_twice_updates_in_place()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN)
        const string uiId = "SCR-IT-UPSERT";

        // 1) 최초 등록.
        var first = await client.PutAsJsonAsync($"{BaseRoute}/{uiId}", new
        {
            title = "초기 제목",
            definitionJson = "{\"v\":1}"
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK,
            $"최초 PUT upsert 가 성공해야 한다. 응답 본문: {await first.Content.ReadAsStringAsync()}");

        // 2) 동일 uiId 재 PUT(갱신) — ON CONFLICT DO UPDATE 로 in-place 갱신돼야 한다.
        var second = await client.PutAsJsonAsync($"{BaseRoute}/{uiId}", new
        {
            title = "갱신된 제목",
            definitionJson = "{\"v\":2}"
        });
        second.StatusCode.Should().Be(HttpStatusCode.OK,
            $"동일 uiId 재 PUT(UPDATE 경로)이 성공해야 한다. 응답 본문: {await second.Content.ReadAsStringAsync()}");

        // 3) GET {uiId} — 갱신 값으로 되읽혀야 한다(중복 행 생성 없이 in-place).
        var fetched = await client.GetFromJsonAsync<ScreenDefinitionDto>($"{BaseRoute}/{uiId}");
        fetched.Should().NotBeNull();
        fetched!.Title.Should().Be("갱신된 제목", "재 PUT 후 Title 이 갱신돼야 한다");
        fetched.DefinitionJson.Should().Be("{\"v\":2}", "재 PUT 후 DefinitionJson 이 갱신돼야 한다");

        // 4) GET list — 동일 uiId 가 단 한 건이어야 한다(UPSERT 이므로 중복 INSERT 금지).
        var list = await client.GetFromJsonAsync<List<ScreenDefinitionDto>>(BaseRoute);
        list.Should().NotBeNull();
        list!.Where(d => d.UiId == uiId).Should().HaveCount(1,
            "UPSERT 는 동일 PK를 in-place 갱신하므로 행이 한 건이어야 한다");
    }

    // ── 검증 (Title/DefinitionJson 공백 400) ─────────────────────────────────────

    [Fact]
    public async Task Put_with_blank_title_is_bad_request()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — 인가는 통과, 유효성에서 400
        var resp = await client.PutAsJsonAsync($"{BaseRoute}/SCR-BLANK-TITLE", new
        {
            title = "   ",
            definitionJson = "{}"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "Title 이 공백이면 400(Title and DefinitionJson are required.)이어야 한다");
    }

    [Fact]
    public async Task Put_with_blank_definitionJson_is_bad_request()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN)
        var resp = await client.PutAsJsonAsync($"{BaseRoute}/SCR-BLANK-JSON", new
        {
            title = "제목 있음",
            definitionJson = ""
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "DefinitionJson 이 공백이면 400 이어야 한다");
    }

    // ── 미존재 GET {uiId} 404 ────────────────────────────────────────────────────

    [Fact]
    public async Task Get_unknown_uiId_returns_not_found()
    {
        var client = _factory.CreateAuthenticatedClient();   // 인증 통과 — 미존재이므로 404
        var resp = await client.GetAsync($"{BaseRoute}/SCR-DOES-NOT-EXIST");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "미존재 uiId 의 GET 은 404 여야 한다(store.GetAsync null → NotFound)");
    }

    // 도메인 직렬화(camelCase). ScreenDefinitionRecord(UiId, Title, DefinitionJson)와 정합.
    private sealed record ScreenDefinitionDto(string UiId, string Title, string DefinitionJson);
}
