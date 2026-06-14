using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.MDM;

/// <summary>
/// MDM 기준정보 마스터 CRUD 인가(authorization) + 영속 회수 HTTP 통합 테스트.
///
/// 기존 MDMControllerIntegrationTests.cs(미인증 401 + ADMIN POST→GET 회수)를 보완해, 각 마스터
/// (areas/products/code-classes/codes)의 쓰기 엔드포인트에 대해 인가 3단(미인증 401 · 권한 부족 403 ·
/// ADMIN 200→영속 회수)을 end-to-end로 단언한다. 403은 인증은 되었으나 perm:mdm:manage 권한이 없는
/// 토큰("fdc:read")으로 발급해, 인증만으로 기준정보 쓰기가 불가함(ADR-003 PEP)을 검증한다.
/// 또한 equipment GET 단건(존재 200·미존재 404)·목록 포함을 검증한다.
///
/// 하니스 사실(TestApiFactory): 클래스별 고유 SQLite 임시 DB(GUID) + Foreign Keys=False(FK 비강제) +
/// db/migrations 부트스트랩. CreateAuthenticatedClient()는 기본 "*"(ADMIN, sub="test-admin")로 발급되어
/// perm:mdm:manage 정책을 통과한다. FK-OFF이므로 부모 선행 시드는 불필요하나, CreateCode는 서비스가
/// 코드클래스 존재를 선조회(GetClassByIdAsync)하므로 코드 생성 전 코드클래스를 먼저 등록한다(부모자식 순서 시드).
///
/// 마이그레이션 정합: 설비(MDM_EQUIPMENT)=V002, 공장/구역/제품/코드클래스/코드=V026.
/// 403/401은 인가가 라우팅·모델바인딩보다 먼저 적용됨을 함께 증명한다.
/// </summary>
public sealed class MdmMasterAuthorizationIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public MdmMasterAuthorizationIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── Areas (MDM_AREA, V026) — POST 인가 3단 + 영속 회수 ──────────────────────────

    [Fact]
    public async Task CreateArea_enforces_authorization_then_persists_for_admin()
    {
        const string plantId = "MDM-AUTHZ-PLANT-AREA";
        const string areaId  = "MDM-AUTHZ-AREA-1";
        var body = new { areaId, areaName = "Authz Area", plantId };

        // 1) 미인증 → 401 ([Authorize]가 라우팅/모델바인딩보다 먼저 적용).
        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/v1/mdm/areas", body)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "토큰 없는 POST areas는 401이어야 한다");

        // 2) 인증되었으나 perm:mdm:manage 없는 토큰("fdc:read") → 403 (인가 정책 집행).
        var limited = _factory.CreateAuthenticatedClient("fdc:read");
        (await limited.PostAsJsonAsync("/api/v1/mdm/areas", body)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden,
                "perm:mdm:manage 권한이 없는 토큰의 POST areas는 403이어야 한다(인증만으로는 기준정보 쓰기 불가)");

        // 3) ADMIN("*") → 200 + 영속화. MDM_AREA INSERT가 SQLite에서 성공해야 한다.
        var admin = _factory.CreateAuthenticatedClient();
        var createResp = await admin.PostAsJsonAsync("/api/v1/mdm/areas", body);
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ADMIN의 구역 등록(MDM_AREA INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // 4) GET(필터 plantId)으로 영속 회수 — INSERT한 행이 SELECT로 되읽혀야 한다.
        var list = await admin.GetFromJsonAsync<List<AreaDto>>($"/api/v1/mdm/areas?plantId={plantId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(a => a.Id == areaId)
            .Which.PlantId.Should().Be(plantId, "INSERT한 PLANT_ID가 그대로 영속·회수돼야 한다");
    }

    // ── Products (MDM_PRODUCT, V026) — POST 인가 3단 + 영속 회수 ─────────────────────

    [Fact]
    public async Task CreateProduct_enforces_authorization_then_persists_for_admin()
    {
        const string productId = "MDM-AUTHZ-PROD-1";
        var body = new { productId, productName = "Authz Product", productType = "WAFER", unit = "EA" };

        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/v1/mdm/products", body)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "토큰 없는 POST products는 401이어야 한다");

        var limited = _factory.CreateAuthenticatedClient("fdc:read");
        (await limited.PostAsJsonAsync("/api/v1/mdm/products", body)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden,
                "perm:mdm:manage 권한이 없는 토큰의 POST products는 403이어야 한다");

        var admin = _factory.CreateAuthenticatedClient();
        var createResp = await admin.PostAsJsonAsync("/api/v1/mdm/products", body);
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ADMIN의 제품 등록(MDM_PRODUCT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // GetAll은 VALID_STATE='Valid'만 반환 — 등록 시 'Valid'로 저장되므로 목록에 보여야 한다.
        var list = await admin.GetFromJsonAsync<List<ProductDto>>("/api/v1/mdm/products");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == productId)
            .Which.ProductType.Should().Be("WAFER", "INSERT한 PRODUCT_TYPE이 그대로 영속·회수돼야 한다");
    }

    // ── Code Classes (MDM_CODE_CLASS, V026) — POST 인가 3단 + 영속 회수 ──────────────

    [Fact]
    public async Task CreateCodeClass_enforces_authorization_then_persists_for_admin()
    {
        const string codeClassId = "MDM-AUTHZ-CC-1";
        var body = new { codeClassId, codeClassName = "Authz Code Class" };

        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/v1/mdm/code-classes", body)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "토큰 없는 POST code-classes는 401이어야 한다");

        var limited = _factory.CreateAuthenticatedClient("fdc:read");
        (await limited.PostAsJsonAsync("/api/v1/mdm/code-classes", body)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden,
                "perm:mdm:manage 권한이 없는 토큰의 POST code-classes는 403이어야 한다");

        var admin = _factory.CreateAuthenticatedClient();
        var createResp = await admin.PostAsJsonAsync("/api/v1/mdm/code-classes", body);
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ADMIN의 코드클래스 등록(MDM_CODE_CLASS INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var list = await admin.GetFromJsonAsync<List<CodeClassDto>>("/api/v1/mdm/code-classes");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(c => c.Id == codeClassId)
            .Which.CodeClassName.Should().Be("Authz Code Class", "INSERT한 CODE_CLASS_NAME이 영속·회수돼야 한다");
    }

    // ── Codes (MDM_CODE, V026) — 부모자식 순서 시드 + POST 인가 3단 + 영속 회수 ────────

    [Fact]
    public async Task CreateCode_enforces_authorization_then_persists_for_admin()
    {
        const string codeClassId = "MDM-AUTHZ-CC-FORCODE";
        const string codeId      = "MDM-AUTHZ-CODE-1";

        // 부모자식 순서 시드: CreateCode는 서비스가 코드클래스 존재를 선조회(GetClassByIdAsync)하므로
        // 코드 생성 전에 코드클래스(부모)를 ADMIN으로 먼저 등록한다.
        var admin = _factory.CreateAuthenticatedClient();
        var classResp = await admin.PostAsJsonAsync("/api/v1/mdm/code-classes", new
        {
            codeClassId,
            codeClassName = "Authz Code Class For Code"
        });
        classResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "코드 생성 전 부모 코드클래스(선조회 대상) 등록이 성공해야 한다");

        var codeBody = new { codeId, codeClassId, codeName = "Authz Code", sortOrder = 1 };

        // 1) 미인증 → 401.
        var anon = _factory.CreateClient();
        (await anon.PostAsJsonAsync("/api/v1/mdm/codes", codeBody)).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "토큰 없는 POST codes는 401이어야 한다");

        // 2) 권한 부족 토큰 → 403. (인가가 코드클래스 선조회보다 먼저 차단되므로 403이어야 한다.)
        var limited = _factory.CreateAuthenticatedClient("fdc:read");
        (await limited.PostAsJsonAsync("/api/v1/mdm/codes", codeBody)).StatusCode
            .Should().Be(HttpStatusCode.Forbidden,
                "perm:mdm:manage 권한이 없는 토큰의 POST codes는 403이어야 한다");

        // 3) ADMIN → 200 + 영속화. MDM_CODE INSERT (FK CODE_CLASS_ID→MDM_CODE_CLASS).
        var createResp = await admin.PostAsJsonAsync("/api/v1/mdm/codes", codeBody);
        var createRespBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"ADMIN의 코드 등록(MDM_CODE INSERT)이 성공해야 한다. 응답 본문: {createRespBody}");

        // 4) GET(필터 codeClassId)으로 영속 회수 — VALID_STATE='Valid'로 저장되어 목록에 보여야 한다.
        var list = await admin.GetFromJsonAsync<List<CodeDto>>($"/api/v1/mdm/codes?codeClassId={codeClassId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(c => c.Id == codeId)
            .Which.CodeClassId.Should().Be(codeClassId, "INSERT한 CODE_CLASS_ID(부모)가 그대로 영속·회수돼야 한다");
    }

    // ── Equipment (MDM_EQUIPMENT, V002) — GET 단건 200·목록 포함·미존재 404 ──────────

    [Fact]
    public async Task GetEquipmentById_returns_single_and_listed_after_create()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — POST는 perm:mdm:manage 통과, GET은 [Authorize]만
        const string plantId     = "MDM-AUTHZ-PLANT-EQ";
        const string equipmentId = "MDM-AUTHZ-EQ-1";

        // 1) 설비 등록(NOT NULL 컬럼 모두 채움; 자기참조 부모 FK는 null).
        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "Authz Equipment",
            plantId,
            areaId = "A-AUTHZ",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-AUTHZ"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // 2) 단건 조회(GET equipment/{id}) → 200 — 라우트 파라미터 바인딩 + SELECT WHERE EQUIPMENT_ID.
        var getResp = await client.GetAsync($"/api/v1/mdm/equipment/{equipmentId}");
        var getBody = await getResp.Content.ReadAsStringAsync();
        getResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"등록된 설비 단건 조회는 200이어야 한다. 응답 본문: {getBody}");

        var single = await getResp.Content.ReadFromJsonAsync<EquipmentDto>();
        single.Should().NotBeNull();
        single!.Id.Should().Be(equipmentId, "단건 GET 응답은 요청한 설비여야 한다");
        single.EquipmentClassId.Should().Be("CLS-AUTHZ", "INSERT한 EQUIPMENT_CLASS_ID가 영속·복원돼야 한다");

        // 3) 목록 조회(GET equipment?plantId=)에 방금 등록한 설비가 포함돼야 한다.
        var list = await client.GetFromJsonAsync<List<EquipmentDto>>($"/api/v1/mdm/equipment?plantId={plantId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(e => e.Id == equipmentId)
            .Which.PlantId.Should().Be(plantId);
    }

    [Fact]
    public async Task GetEquipmentById_returns_404_when_not_found()
    {
        // 단건 조회 미존재 — 서비스가 Error.NotFound를 반환하고 컨트롤러가 NotFound(result.Error)로 매핑한다.
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/mdm/equipment/MDM-AUTHZ-NO-SUCH-EQ");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound,
            $"존재하지 않는 설비 단건 조회는 404여야 한다(GetById null→Error.NotFound→NotFound). 응답 본문: {body}");
    }

    [Fact]
    public async Task GetEquipmentById_requires_auth()
    {
        // GET equipment/{id}도 컨트롤러 기본 [Authorize] 적용 — 토큰 없으면 401.
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/mdm/equipment/ANY-ID")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "토큰 없는 GET equipment/{id}는 401이어야 한다");
    }

    // 도메인 직렬화(JSON camelCase, 대소문자 무시 역직렬화). Id는 Entity<TId> 기반 식별자.
    private sealed record AreaDto(string Id, string AreaName, string Description, string PlantId);

    private sealed record ProductDto(
        string Id, string ProductName, string Description, string ProductType, string Unit, string ValidState);

    private sealed record CodeClassDto(string Id, string CodeClassName, string Description);

    private sealed record CodeDto(
        string Id, string CodeClassId, string CodeName, int SortOrder, string ValidState);

    private sealed record EquipmentDto(
        string Id, string EquipmentName, string PlantId, string AreaId,
        string EquipmentType, string EquipmentClassId, string ValidState);
}
