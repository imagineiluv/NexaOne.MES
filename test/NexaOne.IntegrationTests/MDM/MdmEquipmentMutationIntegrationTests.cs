using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.MDM;

/// <summary>
/// MDM 설비(MDM_EQUIPMENT, V002) 쓰기/상태전이 HTTP 통합 테스트 —
/// 생성(POST) → 수정(PUT) → 비활성화(DELETE 논리삭제) 경로가 SQLite에서 실제로 동작하는지
/// (테이블/컬럼 존재 + 방언 UPDATE + 인증 + 직렬화 + UPDATE 영속화)를 되읽기(GET)로 실증한다.
///
/// 컨트롤러 매핑(MdmController):
///   PUT    equipment/{id}  = UpdateEquipment     → UpdateEquipmentRequest(EquipmentName, Description?, EquipmentType, Vendor?, Model?)
///   DELETE equipment/{id}  = DeactivateEquipment → 본문 없음, 성공 시 204 NoContent (논리삭제: ValidState='Invalid')
/// 두 경로 모두 [Authorize(Policy="perm:mdm:manage")] — 미인증은 401.
///
/// 전이 대상 행을 테스트가 먼저 CreateEquipment로 생성하므로 FK-OFF 하버스트와 무관하게 성립한다.
/// (자기참조 부모 FK는 null로 둔다.)
///
/// ── 주의: 읽기경로 상태손실 후보 ─────────────────────────────────────────────────────
/// EquipmentRepository.EquipmentRow.ToDomain()이 Equipment.Create(...)를 호출하는데,
/// Create는 (1) Description 인자를 받지 않아 항상 빈 문자열로 재구성하고 (2) ValidState를 무조건 "Valid"로
/// 하드코딩한다. 따라서 DB에 VALID_STATE='Invalid'(비활성화) 또는 DESCRIPTION이 저장돼 있어도,
/// GetById/GetAll 되읽기 시 도메인→DTO에서 그 값이 소실된다. 아래 두 전이 테스트의 되읽기 단언이
/// 이 갭(UPDATE는 영속되나 읽기경로가 상태를 복원하지 못함)을 회귀 검증/노출한다.
/// </summary>
public sealed class MdmEquipmentMutationIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public MdmEquipmentMutationIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task UpdateEquipment_requires_auth()
    {
        // PUT equipment/{id}는 [Authorize(Policy="perm:mdm:manage")] — 토큰 없이 401이어야 한다
        // ([Authorize]가 라우팅·모델바인딩보다 먼저 적용되는지 검증).
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsJsonAsync("/api/v1/mdm/equipment/ANY-ID", new
        {
            equipmentName = "X",
            equipmentType = "GENERIC"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "인증 없는 PUT equipment는 401이어야 한다");
    }

    [Fact]
    public async Task DeactivateEquipment_requires_auth()
    {
        // DELETE equipment/{id}도 perm:mdm:manage — 토큰 없이 401.
        var anon = _factory.CreateClient();
        var resp = await anon.DeleteAsync("/api/v1/mdm/equipment/ANY-ID");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "인증 없는 DELETE equipment는 401이어야 한다");
    }

    [Fact]
    public async Task CreateEquipment_then_UpdateEquipment_persists_changes()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:mdm:manage 통과
        const string plantId = "P-MUT-UPD";
        const string equipmentId = "MDM-MUT-UPD-1";

        // 1) 생성 — MDM_EQUIPMENT INSERT (NOT NULL 컬럼을 모두 채운다).
        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "Original Name",
            plantId,
            areaId = "A-MUT",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-MUT"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 생성(MDM_EQUIPMENT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // 2) 수정 — PUT equipment/{id}. UpdateEquipmentRequest와 1:1로 본문을 맞춘다.
        //    서비스가 GetById→UpdateInfo→UpdateAsync(UPDATE ... SET EQUIPMENT_NAME/EQUIPMENT_TYPE/VENDOR/MODEL/...)로 영속.
        var updateResp = await client.PutAsJsonAsync($"/api/v1/mdm/equipment/{equipmentId}", new
        {
            equipmentName = "Updated Name",
            description = "Updated Description",
            equipmentType = "ROBOT",
            vendor = "ACME",
            model = "X-9000"
        });
        var updateBody = await updateResp.Content.ReadAsStringAsync();
        updateResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 수정(MDM_EQUIPMENT UPDATE)이 SQLite에서 성공해야 한다(방언/컬럼 정합). 응답 본문: {updateBody}");

        // 3) 단건 되읽기 — UPDATE가 실제로 영속되어 변경 필드가 SELECT로 복원되는지 검증.
        var getResp = await client.GetAsync($"/api/v1/mdm/equipment/{equipmentId}");
        var getBody = await getResp.Content.ReadAsStringAsync();
        getResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"수정 후 단건 조회(MDM_EQUIPMENT SELECT)가 성공해야 한다. 응답 본문: {getBody}");

        var single = await getResp.Content.ReadFromJsonAsync<EquipmentDto>();
        single.Should().NotBeNull();
        single!.Id.Should().Be(equipmentId);

        // UPDATE 영속 + 읽기경로 복원이 성립하는 필드(EquipmentRow가 ToDomain에서 전달하는 값).
        single.EquipmentName.Should().Be("Updated Name",
            $"PUT의 EquipmentName UPDATE가 영속되어 되읽기에 반영돼야 한다. 응답 본문: {getBody}");
        single.EquipmentType.Should().Be("ROBOT",
            $"PUT의 EquipmentType UPDATE가 영속되어 되읽기에 반영돼야 한다. 응답 본문: {getBody}");

        // 읽기경로 상태손실 후보: DESCRIPTION은 DB에 UPDATE되지만 EquipmentRow.ToDomain()이
        // Equipment.Create로 재구성하며 description 인자를 전달하지 않아(항상 "") 되읽기에서 소실된다.
        // 이 단언이 실패하면 제품 버그(읽기경로 상태손실)다 — assert 메시지에 본문 포함.
        single.Description.Should().Be("Updated Description",
            $"PUT의 Description UPDATE가 되읽기에 반영돼야 한다(읽기경로가 DESCRIPTION을 복원해야 함). 응답 본문: {getBody}");
    }

    [Fact]
    public async Task DeactivateEquipment_logical_delete_reflects_invalid_state_on_read()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN)
        const string plantId = "P-MUT-DEL";
        const string equipmentId = "MDM-MUT-DEL-1";

        // 1) 생성 — 비활성화 대상 행을 먼저 만든다.
        var createResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "To Deactivate",
            plantId,
            areaId = "A-MUT",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-MUT"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"설비 생성(MDM_EQUIPMENT INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        // 2) 비활성화(DELETE) — 논리삭제: 서비스가 GetById→Deactivate()(ValidState='Invalid')→UpdateAsync.
        //    컨트롤러는 성공 시 204 NoContent. (FK-OFF 무관 — 대상 행을 위에서 만들었다.)
        var delResp = await client.DeleteAsync($"/api/v1/mdm/equipment/{equipmentId}");
        var delBody = await delResp.Content.ReadAsStringAsync();
        delResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"설비 비활성화(MDM_EQUIPMENT UPDATE VALID_STATE)가 성공해 204여야 한다. 응답 본문: {delBody}");

        // 3) 되읽기 — 논리삭제이므로 행은 여전히 존재(물리 삭제 아님)해 단건 GET은 200이어야 한다.
        var getResp = await client.GetAsync($"/api/v1/mdm/equipment/{equipmentId}");
        var getBody = await getResp.Content.ReadAsStringAsync();
        getResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"논리삭제 후에도 행은 존재하므로 단건 조회는 200이어야 한다(물리삭제 아님). 응답 본문: {getBody}");

        var single = await getResp.Content.ReadFromJsonAsync<EquipmentDto>();
        single.Should().NotBeNull();
        single!.Id.Should().Be(equipmentId);

        // 읽기경로 상태손실 후보: Deactivate가 DB에 VALID_STATE='Invalid'를 UPDATE하지만,
        // EquipmentRow.ToDomain()이 Equipment.Create로 재구성하며 ValidState를 무조건 "Valid"로 하드코딩해
        // 되읽기 시 'Invalid' 상태가 소실된다. 이 단언이 실패하면 제품 버그(논리삭제 상태가 읽기경로에서 사라짐).
        single.ValidState.Should().Be("Invalid",
            $"비활성화(논리삭제) 후 되읽기에서 ValidState='Invalid'가 반영돼야 한다(읽기경로가 VALID_STATE를 복원해야 함). 응답 본문: {getBody}");
    }

    // 도메인 직렬화(JSON camelCase, 대소문자 무시 역직렬화). Id는 Entity<TId> 기반 식별자.
    private sealed record EquipmentDto(
        string Id, string EquipmentName, string Description, string PlantId, string AreaId,
        string EquipmentType, string EquipmentClassId, string ValidState);
}
