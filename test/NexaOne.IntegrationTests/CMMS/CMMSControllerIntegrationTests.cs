using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.CMMS;

/// <summary>
/// CMMS(보전) 모듈 HTTP 통합 테스트 — V008 마이그레이션으로 생성되는 CMMS_WORK_ORDER 테이블이
/// SQLite 테스트 하니스에서 실제로 동작하는지(테이블 존재 + SELECT 방언 + 인증/라우팅/직렬화),
/// 그리고 작업지시(Work Order) 생성→조회 happy-path가 end-to-end로 동작하는지 검증한다.
///
/// 주의: maintenance-plans / spare-parts 엔드포인트는 대상 테이블
/// (CMMS_MAINTENANCE_PLAN / CMMS_SPARE_PART)의 마이그레이션(V*.sql)이 없어
/// SqliteSchemaBootstrapper가 테이블을 만들지 못한다 → 스모크 대상에서 제외한다(suspectedBugs 참조).
/// 검증 가능한 list GET은 work-orders 뿐이다.
///
/// CMMS_WORK_ORDER.EQUIPMENT_ID는 MDM_EQUIPMENT를 FK 참조하므로(운영 무결성) 쓰기 테스트에서는
/// 먼저 설비를 등록한다(EST 참조 패턴과 동일). PLAN_ID는 NULL 허용이라 시드 불필요.
/// </summary>
public sealed class CMMSControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public CMMSControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ──────────────────────────────────────────────────────────────────────────
    // (a) Read smoke — work-orders list GET
    //     인증 없으면 401, ADMIN 토큰이면 200(빈 목록 OK). 시드 불필요(읽기 전용).
    //     이는 CMMS_WORK_ORDER 테이블 존재 + SQLite SELECT 방언 + 인증 + 라우팅 + 직렬화를 한 번에 증명한다.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetWorkOrders_requires_auth_and_returns_empty_list_for_admin()
    {
        // status/from/to/equipmentId 모두 옵션. status 미지정 시 equipmentId(빈 문자열)로 조회 →
        // WHERE EQUIPMENT_ID = '' 가 빈 목록을 200으로 돌려준다(시드 없이 bulletproof).
        const string path = "/api/v1/cmms/work-orders";

        // 토큰 없는 클라이언트 → [Authorize]에 의해 401.
        var anon = _factory.CreateClient();
        var anonResp = await anon.GetAsync(path);
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]가 붙은 GET work-orders는 토큰 없이 401이어야 한다");

        // ADMIN("*") 토큰 → 200 + 빈 배열.
        var admin = _factory.CreateAuthenticatedClient();
        var adminResp = await admin.GetAsync(path);
        var body = await adminResp.Content.ReadAsStringAsync();
        adminResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_WORK_ORDER 테이블 SELECT가 성공해 200이어야 한다. 응답 본문: {body}");

        var list = await adminResp.Content.ReadFromJsonAsync<List<WorkOrderDto>>();
        list.Should().NotBeNull("200 응답은 JSON 배열로 역직렬화되어야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (b) Write happy-path — 설비(FK 부모) 등록 → 작업지시 생성 → 조회 확인.
    //     WoType은 도메인 검증상 "PM" 또는 "CM"만 허용된다.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateWorkOrder_persists_and_is_retrievable_by_equipment()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — mdm/cmms:manage 정책 통과
        const string plantId = "P-CMMS";
        const string equipmentId = "EQ-CMMS-1";
        const string woId = "CMMS-IT-1";

        // 0) 설비 등록 — CMMS_WORK_ORDER.EQUIPMENT_ID는 MDM_EQUIPMENT FK라 선행되어야 한다.
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "CMMS Test Equipment",
            plantId,
            areaId = "A-CMMS",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-CMMS"
        });
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다");

        // 1) 작업지시 생성 — CreateWorkOrderRequest(WoId, EquipmentId, WoType, Description, AssigneeId).
        //    WoType은 'PM'(예방보전) 또는 'CM'(사후보전)만 유효.
        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/work-orders", new
        {
            woId,
            equipmentId,
            woType = "PM",
            description = "Preventive maintenance work order",
            assigneeId = "USER-CMMS-1"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_WORK_ORDER INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<WorkOrderDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(woId);
        created.EquipmentId.Should().Be(equipmentId);
        created.WoType.Should().Be("PM");
        created.Status.Should().Be("Issued", "생성 직후 상태는 Issued여야 한다");

        // 2) 설비별 조회(CMMS_WORK_ORDER SELECT) — 방금 만든 작업지시가 돌아오는지 확인.
        var list = await client.GetFromJsonAsync<List<WorkOrderDto>>(
            $"/api/v1/cmms/work-orders?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(w => w.Id == woId)
            .Which.EquipmentId.Should().Be(equipmentId);
    }

    // WorkOrder 도메인(JSON camelCase, 대소문자 무시 역직렬화)에서 필요한 필드만 매핑.
    private sealed record WorkOrderDto(string Id, string EquipmentId, string WoType, string Status);
}
