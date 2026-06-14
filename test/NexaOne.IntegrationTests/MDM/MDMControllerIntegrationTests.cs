using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.MDM;

/// <summary>
/// MDM 기준정보 HTTP 통합 테스트 — V002 마이그레이션으로 생성되는 MDM_EQUIPMENT 테이블이
/// SQLite에서 실제로 동작하는지(테이블 존재 + 방언 SELECT/INSERT + 인증 + 라우팅 + 직렬화)를
/// end-to-end로 검증한다.
///
/// 참고: 이 모듈의 1차 엔티티(설비)만 마이그레이션 테이블(MDM_EQUIPMENT)을 가진다.
/// plants/areas/products/code-classes/codes 엔드포인트가 참조하는 MDM_PLANT/MDM_AREA/
/// MDM_PRODUCT/MDM_CODE_CLASS/MDM_CODE 테이블은 db/migrations에 존재하지 않으므로(아래 노트 참조)
/// 읽기 스모크/쓰기 해피패스 모두 설비 엔드포인트에 한정한다.
///
/// MDM_EQUIPMENT의 FK는 자기참조(PARENT_EQUIPMENT_ID)뿐이며(부모는 null로 둠), PLANT_ID/AREA_ID는
/// NOT NULL이지만 FK 제약이 없다. 또한 테스트 하버스트는 PRAGMA foreign_keys = OFF로 부트스트랩하므로
/// FK 부모 선행 시드가 필요 없다.
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

    // Equipment 도메인 직렬화(JSON camelCase, 대소문자 무시 역직렬화). Id는 Entity 기반 식별자.
    private sealed record EquipmentDto(
        string Id, string EquipmentName, string PlantId, string AreaId,
        string EquipmentType, string EquipmentClassId, string ValidState);
}
