using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.MDM;

/// <summary>
/// MDM 기준정보 HTTP 통합 테스트 — 마이그레이션으로 생성되는 MDM 마스터 테이블들이
/// SQLite에서 실제로 동작하는지(테이블 존재 + 방언 SELECT/INSERT + 인증 + 라우팅 + 직렬화)를
/// end-to-end로 검증한다.
///
/// 설비(MDM_EQUIPMENT)는 V002, 공장/구역/제품/코드클래스/코드(MDM_PLANT/MDM_AREA/MDM_PRODUCT/
/// MDM_CODE_CLASS/MDM_CODE)는 V026 마이그레이션으로 생성된다. V026 추가 전에는 후자 5개 테이블이
/// 누락되어 plants/areas/products/code-classes/codes 엔드포인트가 신규 DB에서 깨졌다 —
/// 아래 읽기 스모크/쓰기 해피패스가 그 갭(테이블 존재 + 방언 SELECT/INSERT)을 회귀 검증한다.
///
/// FK(MDM_AREA→MDM_PLANT, MDM_CODE→MDM_CODE_CLASS, MDM_EQUIPMENT 자기참조)는 테이블 생성 시
/// 파싱으로만 검증되며, 테스트 하버스트는 PRAGMA foreign_keys = OFF로 부트스트랩하므로
/// FK 부모 선행 시드가 필요 없다(읽기는 시드 불필요). 단, CreateCode는 서비스가 코드클래스 존재를
/// 선조회하므로 코드 생성 전에 코드클래스를 먼저 등록한다.
/// </summary>
public sealed class MDMControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public MDMControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetEquipment_requires_auth_and_returns_ok_for_admin()
    {
        const string plantId = "P-MDM";

        // 1) 미인증 클라이언트는 401 — [Authorize]가 라우팅보다 먼저 적용되는지 검증.
        var anon = _factory.CreateClient();
        var anonResp = await anon.GetAsync($"/api/v1/mdm/equipment?plantId={plantId}");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]가 적용된 GET equipment는 토큰 없이 401이어야 한다");

        // 2) ADMIN("*") 토큰은 200 — MDM_EQUIPMENT 테이블 존재 + SQLite SELECT + 직렬화가 성공해야 한다.
        //    시드 없이 빈 목록이어도 무방하다(읽기 전용 스모크).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync($"/api/v1/mdm/equipment?plantId={plantId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"MDM_EQUIPMENT SELECT가 SQLite에서 성공해야 한다. 응답 본문: {body}");

        var list = await resp.Content.ReadFromJsonAsync<List<EquipmentDto>>();
        list.Should().NotBeNull("Ok(result.Value)는 설비 목록 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task CreateEquipment_persists_and_is_retrievable()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — perm:mdm:manage 정책 통과
        const string plantId = "P-MDM";
        const string equipmentId = "MDM-IT-1";

        // 1) 설비 등록 — MDM_EQUIPMENT INSERT. PLANT_ID/AREA_ID/EQUIPMENT_CLASS_ID는 NOT NULL이라
        //    비어있지 않은 값을 채운다(자기참조 부모 FK는 null로 두므로 선행 시드 불필요).
        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "MDM Test Equipment",
            plantId,
            areaId = "A-MDM",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-MDM"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<EquipmentDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(equipmentId, "POST 응답은 등록된 설비(Result.Value)여야 한다");
        created.EquipmentName.Should().Be("MDM Test Equipment");

        // 2) 목록 조회(MDM_EQUIPMENT SELECT WHERE PLANT_ID) — 방금 등록한 설비가 영속화됐는지 확인.
        var list = await client.GetFromJsonAsync<List<EquipmentDto>>(
            $"/api/v1/mdm/equipment?plantId={plantId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(e => e.Id == equipmentId)
            .Which.PlantId.Should().Be(plantId);

        // 3) 단건 조회(MDM_EQUIPMENT SELECT WHERE EQUIPMENT_ID) — 라우트 파라미터 바인딩 검증.
        var single = await client.GetFromJsonAsync<EquipmentDto>($"/api/v1/mdm/equipment/{equipmentId}");
        single.Should().NotBeNull();
        single!.Id.Should().Be(equipmentId);
        single.EquipmentClassId.Should().Be("CLS-MDM");
    }

    // ── Plants (MDM_PLANT, V026) ────────────────────────────────────────────────

    [Fact]
    public async Task GetPlants_requires_auth_and_returns_ok()
    {
        // 1) 미인증 → 401([Authorize]가 라우팅보다 먼저 적용).
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/mdm/plants")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET plants는 토큰 없이 401이어야 한다");

        // 2) ADMIN → 200 — MDM_PLANT 테이블 존재 + SQLite SELECT + 직렬화 성공(빈 목록도 무방).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/mdm/plants");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"MDM_PLANT SELECT가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<PlantDto>>())
            .Should().NotBeNull("plants 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task CreatePlant_persists_and_is_listed()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:mdm:manage 통과
        const string plantId = "MDM-IT-PLANT-1";

        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/plants", new
        {
            plantId,
            plantName = "MDM Test Plant",
            country = "KR",
            timeZone = "Asia/Seoul"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"공장 등록(MDM_PLANT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<PlantDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(plantId, "POST 응답은 등록된 공장(Result.Value)이어야 한다");
        created.PlantName.Should().Be("MDM Test Plant");

        var list = await client.GetFromJsonAsync<List<PlantDto>>("/api/v1/mdm/plants");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == plantId)
            .Which.PlantName.Should().Be("MDM Test Plant");
    }

    // ── Areas (MDM_AREA, V026) ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAreas_requires_auth_and_returns_ok()
    {
        const string plantId = "MDM-IT-PLANT-AREASMOKE";

        var anon = _factory.CreateClient();
        (await anon.GetAsync($"/api/v1/mdm/areas?plantId={plantId}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET areas는 토큰 없이 401이어야 한다");

        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync($"/api/v1/mdm/areas?plantId={plantId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"MDM_AREA SELECT(WHERE PLANT_ID)가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<AreaDto>>())
            .Should().NotBeNull("areas 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task CreateArea_persists_and_is_listed_by_plant()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string plantId = "MDM-IT-PLANT-AREA";
        const string areaId = "MDM-IT-AREA-1";

        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/areas", new
        {
            areaId,
            areaName = "MDM Test Area",
            plantId
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"구역 등록(MDM_AREA INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<AreaDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(areaId);

        var list = await client.GetFromJsonAsync<List<AreaDto>>($"/api/v1/mdm/areas?plantId={plantId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(a => a.Id == areaId)
            .Which.PlantId.Should().Be(plantId);
    }

    // ── Products (MDM_PRODUCT, V026) ────────────────────────────────────────────

    [Fact]
    public async Task GetProducts_requires_auth_and_returns_ok()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/mdm/products")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET products는 토큰 없이 401이어야 한다");

        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/mdm/products");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"MDM_PRODUCT SELECT(WHERE VALID_STATE)가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<ProductDto>>())
            .Should().NotBeNull("products 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task CreateProduct_persists_and_is_listed()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string productId = "MDM-IT-PROD-1";

        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/products", new
        {
            productId,
            productName = "MDM Test Product",
            productType = "WAFER",
            unit = "EA"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"제품 등록(MDM_PRODUCT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<ProductDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(productId);

        // GetAll은 VALID_STATE='Valid'만 반환 — 등록 시 'Valid'로 저장되므로 목록에 보여야 한다.
        var list = await client.GetFromJsonAsync<List<ProductDto>>("/api/v1/mdm/products");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == productId)
            .Which.ProductType.Should().Be("WAFER");
    }

    // ── Code Classes (MDM_CODE_CLASS, V026) ─────────────────────────────────────

    [Fact]
    public async Task GetCodeClasses_requires_auth_and_returns_ok()
    {
        var anon = _factory.CreateClient();
        (await anon.GetAsync("/api/v1/mdm/code-classes")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET code-classes는 토큰 없이 401이어야 한다");

        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync("/api/v1/mdm/code-classes");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"MDM_CODE_CLASS SELECT가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<CodeClassDto>>())
            .Should().NotBeNull("code-classes 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task CreateCodeClass_persists_and_is_listed()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string codeClassId = "MDM-IT-CC-1";

        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/code-classes", new
        {
            codeClassId,
            codeClassName = "MDM Test Code Class"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"코드클래스 등록(MDM_CODE_CLASS INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<CodeClassDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(codeClassId);

        var list = await client.GetFromJsonAsync<List<CodeClassDto>>("/api/v1/mdm/code-classes");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(c => c.Id == codeClassId)
            .Which.CodeClassName.Should().Be("MDM Test Code Class");
    }

    // ── Codes (MDM_CODE, V026) ──────────────────────────────────────────────────

    [Fact]
    public async Task GetCodes_requires_auth_and_returns_ok()
    {
        const string codeClassId = "MDM-IT-CC-SMOKE";

        var anon = _factory.CreateClient();
        (await anon.GetAsync($"/api/v1/mdm/codes?codeClassId={codeClassId}")).StatusCode
            .Should().Be(HttpStatusCode.Unauthorized, "GET codes는 토큰 없이 401이어야 한다");

        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.GetAsync($"/api/v1/mdm/codes?codeClassId={codeClassId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"MDM_CODE SELECT(WHERE CODE_CLASS_ID AND VALID_STATE)가 SQLite에서 성공해야 한다. 응답 본문: {body}");
        (await resp.Content.ReadFromJsonAsync<List<CodeDto>>())
            .Should().NotBeNull("codes 응답은 JSON 배열이어야 한다");
    }

    [Fact]
    public async Task CreateCode_persists_and_is_listed_by_class()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string codeClassId = "MDM-IT-CC-FORCODE";
        const string codeId = "MDM-IT-CODE-1";

        // CreateCode는 서비스가 코드클래스 존재를 선조회(GetClassByIdAsync)하므로 클래스를 먼저 등록한다.
        var classResp = await client.PostAsJsonAsync("/api/v1/mdm/code-classes", new
        {
            codeClassId,
            codeClassName = "MDM Code Class For Code"
        });
        classResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "코드 생성 전 코드클래스 등록(선조회 대상)이 성공해야 한다");

        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/codes", new
        {
            codeId,
            codeClassId,
            codeName = "MDM Test Code",
            sortOrder = 1
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"코드 등록(MDM_CODE INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<CodeDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(codeId);

        var list = await client.GetFromJsonAsync<List<CodeDto>>($"/api/v1/mdm/codes?codeClassId={codeClassId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(c => c.Id == codeId)
            .Which.CodeClassId.Should().Be(codeClassId);
    }

    // 도메인 직렬화(JSON camelCase, 대소문자 무시 역직렬화). Id는 Entity<TId> 기반 식별자.
    private sealed record EquipmentDto(
        string Id, string EquipmentName, string PlantId, string AreaId,
        string EquipmentType, string EquipmentClassId, string ValidState);

    private sealed record PlantDto(
        string Id, string PlantName, string Description, string Country, string TimeZone);

    private sealed record AreaDto(
        string Id, string AreaName, string Description, string PlantId);

    private sealed record ProductDto(
        string Id, string ProductName, string Description, string ProductType, string Unit, string ValidState);

    private sealed record CodeClassDto(
        string Id, string CodeClassName, string Description);

    private sealed record CodeDto(
        string Id, string CodeClassId, string CodeName, int SortOrder, string ValidState);
}
