using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.CMMS;

/// <summary>
/// CMMS(보전) 모듈 HTTP 통합 테스트 — V008(CMMS_WORK_ORDER) + V027(CMMS_MAINTENANCE_PLAN /
/// CMMS_SPARE_PART) 마이그레이션으로 생성되는 테이블들이 SQLite 테스트 하니스에서 실제로 동작하는지
/// (테이블 존재 + SELECT 방언 + 인증/라우팅/직렬화), 그리고 작업지시/정비계획/예비품의 생성→조회
/// happy-path가 end-to-end로 동작하는지 검증한다.
///
/// V027 추가 전에는 maintenance-plans / spare-parts 엔드포인트가 대상 테이블 부재로 'no such table'에
/// 실패했다. 이제 두 테이블이 생기므로 list GET 스모크(401/200) + 쓰기 happy-path를 추가한다.
///
/// FK 참조 테이블은 MDM_EQUIPMENT(설비)·SYS_USER(담당자)지만 테스트 하니스는 FK를 끄므로
/// (TestApiFactory: Foreign Keys=False) 읽기 스모크·쓰기 테스트 모두 부모 행 시드가 필요 없다.
/// 단, work-order 쓰기 테스트는 기존 패턴 유지를 위해 설비를 선등록한다(검증 안정성).
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

    // ──────────────────────────────────────────────────────────────────────────
    // (c) Read smoke — maintenance-plans list GET (V027: CMMS_MAINTENANCE_PLAN).
    //     status 미지정 → equipmentId(빈 문자열)로 GetByEquipment 조회 → WHERE EQUIPMENT_ID = ''
    //     가 빈 목록을 200으로 돌려준다(시드 불필요). 테이블 존재 + SELECT 방언 + 인증/라우팅/직렬화 증명.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetMaintenancePlans_requires_auth_and_returns_empty_list_for_admin()
    {
        const string path = "/api/v1/cmms/maintenance-plans?equipmentId=";

        var anon = _factory.CreateClient();
        var anonResp = await anon.GetAsync(path);
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]가 붙은 GET maintenance-plans는 토큰 없이 401이어야 한다");

        var admin = _factory.CreateAuthenticatedClient();
        var adminResp = await admin.GetAsync(path);
        var body = await adminResp.Content.ReadAsStringAsync();
        adminResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_MAINTENANCE_PLAN 테이블 SELECT가 성공해 200이어야 한다. 응답 본문: {body}");

        var list = await adminResp.Content.ReadFromJsonAsync<List<MaintenancePlanDto>>();
        list.Should().NotBeNull("200 응답은 JSON 배열로 역직렬화되어야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (d) Read smoke — spare-parts list GET (V027: CMMS_SPARE_PART).
    //     lowStock 기본 false → GetAll → ORDER BY PART_NAME. 시드 없이 빈 목록 200.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task GetSpareParts_requires_auth_and_returns_empty_list_for_admin()
    {
        const string path = "/api/v1/cmms/spare-parts";

        var anon = _factory.CreateClient();
        var anonResp = await anon.GetAsync(path);
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize]가 붙은 GET spare-parts는 토큰 없이 401이어야 한다");

        var admin = _factory.CreateAuthenticatedClient();
        var adminResp = await admin.GetAsync(path);
        var body = await adminResp.Content.ReadAsStringAsync();
        adminResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_SPARE_PART 테이블 SELECT가 성공해 200이어야 한다. 응답 본문: {body}");

        // lowStock=true 경로(WHERE CURRENT_STOCK <= MIN_STOCK)도 방언상 동작하는지 함께 증명.
        var lowResp = await admin.GetAsync(path + "?lowStock=true");
        var lowBody = await lowResp.Content.ReadAsStringAsync();
        lowResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"GetLowStock SELECT(<=)가 성공해 200이어야 한다. 응답 본문: {lowBody}");

        var list = await adminResp.Content.ReadFromJsonAsync<List<SparePartDto>>();
        list.Should().NotBeNull("200 응답은 JSON 배열로 역직렬화되어야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (e) Write happy-path — 정비계획 생성 → 설비별 조회 확인 (V027: CMMS_MAINTENANCE_PLAN INSERT/SELECT).
    //     EQUIPMENT_ID→MDM_EQUIPMENT, ASSIGNEE_ID→SYS_USER FK는 하니스에서 OFF라 시드 불필요.
    //     PlanType은 'PM'|'CM', CycleType은 'Daily'|'Weekly'|'Monthly'|'Yearly'만 도메인 허용.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateMaintenancePlan_persists_and_is_retrievable_by_equipment()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — cmms:manage 정책 통과
        const string equipmentId = "EQ-CMMS-PLAN-1";
        const string planId = "CMMS-PLAN-IT-1";

        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/maintenance-plans", new
        {
            planId,
            planName = "Quarterly Preventive Maintenance",
            equipmentId,
            planType = "PM",
            cycleType = "Monthly",
            scheduledDate = new DateTime(2026, 7, 1, 9, 0, 0, DateTimeKind.Utc),
            estimatedDurationHours = 4.5m,
            assigneeId = "USER-CMMS-PLAN-1"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_MAINTENANCE_PLAN INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<MaintenancePlanDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(planId);
        created.EquipmentId.Should().Be(equipmentId);
        created.Status.Should().Be("Planned", "생성 직후 상태는 Planned여야 한다");

        // 설비별 조회(SELECT * WHERE EQUIPMENT_ID = @ ORDER BY SCHEDULED_DATE) — 방금 만든 계획 회수.
        var list = await client.GetFromJsonAsync<List<MaintenancePlanDto>>(
            $"/api/v1/cmms/maintenance-plans?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == planId)
            .Which.EquipmentId.Should().Be(equipmentId);
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (f) Write happy-path — 예비품 생성 → 전체 조회 확인 (V027: CMMS_SPARE_PART INSERT/SELECT).
    //     FK 체인 없음(가장 깨끗). MaxStock > MinStock, Stock >= 0 도메인 검증 충족.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CreateSparePart_persists_and_is_retrievable_in_list()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string partId = "CMMS-PART-IT-1";

        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/spare-parts", new
        {
            partId,
            partName = "Bearing 6205-2RS",
            partNumber = "BRG-6205-2RS",
            description = "Sealed deep-groove ball bearing",
            unitOfMeasure = "EA",
            currentStock = 10m,
            minStock = 2m,
            maxStock = 50m,
            location = "WH-A-01",
            equipmentClassId = (string?)null   // NULL 허용 컬럼 경로 확인
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_SPARE_PART INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<SparePartDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(partId);
        created.PartNumber.Should().Be("BRG-6205-2RS");

        // 전체 조회(SELECT * ORDER BY PART_NAME) — 방금 만든 예비품이 목록에 포함되는지 확인.
        var list = await client.GetFromJsonAsync<List<SparePartDto>>("/api/v1/cmms/spare-parts");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == partId)
            .Which.PartNumber.Should().Be("BRG-6205-2RS");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (g) 읽기경로 회귀 — 작업지시 시작(Issued→InProgress) 후 되읽었을 때 상태가 유지되는지 검증.
    //     WorkOrderRepository.ToDomain이 Create로 재구성하면 Status가 Issued로 유실되던 버그 방지.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task StartWorkOrder_then_read_back_preserves_in_progress_status()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string equipmentId = "EQ-CMMS-STATE-1";
        const string woId = "CMMS-WO-STATE-1";

        await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId, equipmentName = "State WO Equipment", plantId = "P-CMMS-STATE",
            areaId = "A", equipmentType = "GENERIC", equipmentClassId = "CLS"
        });
        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/work-orders", new
        {
            woId, equipmentId, woType = "PM", description = "state test", assigneeId = "U1"
        });
        createResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Issued → InProgress 전이.
        var startResp = await client.PutAsync($"/api/v1/cmms/work-orders/{woId}/start", null);
        startResp.StatusCode.Should().Be(HttpStatusCode.NoContent, "작업지시 시작 전이가 성공해야 한다");

        // 되읽기 — 전이된 상태(InProgress)가 그대로 복원되어야 한다(Issued로 유실되면 안 됨).
        var list = await client.GetFromJsonAsync<List<WorkOrderDto>>(
            $"/api/v1/cmms/work-orders?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(w => w.Id == woId)
            .Which.Status.Should().Be("InProgress",
                "되읽은 작업지시 상태는 전이 결과(InProgress)여야 한다 — ToDomain이 상태를 유실하면 안 된다");
    }

    // 도메인(JSON camelCase, 대소문자 무시 역직렬화)에서 필요한 필드만 매핑.
    private sealed record WorkOrderDto(string Id, string EquipmentId, string WoType, string Status);
    private sealed record MaintenancePlanDto(string Id, string EquipmentId, string Status);
    private sealed record SparePartDto(string Id, string PartNumber);
}
