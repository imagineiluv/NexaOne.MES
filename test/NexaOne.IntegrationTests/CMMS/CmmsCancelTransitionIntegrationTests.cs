using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.CMMS;

/// <summary>
/// CMMS(보전) 모듈 "취소(Cancel)" 상태전이 HTTP 통합 테스트 — 기존 통합 커버리지가 없던 두 보안
/// 전이 엔드포인트를 create→transition→GET 왕복으로 실증한다.
///
/// 검증 대상(미검증 전이 경로):
///   - PUT /api/v1/cmms/work-orders/{woId}/cancel        : CMMS_WORK_ORDER  UPDATE(STATUS=Cancelled) + 읽기경로 복원
///   - PUT /api/v1/cmms/maintenance-plans/{planId}/cancel : CMMS_MAINTENANCE_PLAN UPDATE(STATUS=Cancelled) + 읽기경로 복원
///
/// 핵심 가치:
///   (1) Cancel UPDATE SQL이 SQLite 방언에서 실제로 행을 갱신하고 GET 되읽기로 영속이 확인되는지.
///   (2) 읽기경로(Repository.ToDomain) 상태손실 회귀 — WorkOrder는 Restore, MaintenancePlan은 Restore로
///       DB STATUS를 직접 복원한다. 되읽은 status가 정확히 "Cancelled"인지 단언해 복원 손실을 잡는다.
///       (만약 Create로 재구성하면 작업지시는 Issued·계획은 Planned로 되돌아가 본 단언이 실패한다.)
///   (3) 비인증 401 게이트(두 cancel 엔드포인트는 [Authorize(Policy="perm:cmms:manage")]).
///
/// 도메인 근거: WorkOrder.Cancel / MaintenancePlan.Cancel은 Completed·Cancelled가 아니면 허용되므로
/// 초기상태(Issued / Planned)에서 곧바로 취소 가능 → 중간 Start 없이 create→cancel이 성립한다.
///
/// FK(EQUIPMENT_ID→MDM_EQUIPMENT, ASSIGNEE_ID→SYS_USER)는 하니스에서 OFF(Foreign Keys=False)라
/// 부모 시드 없이 성립한다. work-order는 기존 CMMS 파일 컨벤션을 따라 설비를 선등록한다(검증 안정성).
///
/// 새 파일(충돌 방지) — 기존 CMMS 통합 테스트 파일들은 수정하지 않는다.
/// 클래스 병렬 실행 간 간섭 방지를 위해 ID는 본 파일 전용 고유 접두사(CANCEL-)를 쓴다.
/// </summary>
public sealed class CmmsCancelTransitionIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public CmmsCancelTransitionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ──────────────────────────────────────────────────────────────────────────
    // (a) 비인증 게이트 — work-order cancel은 토큰 없이 401이어야 한다.
    //     대상 행이 없어도 인증 미들웨어가 먼저 401을 반환하므로 시드 불필요.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CancelWorkOrder_without_auth_returns_401()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsync("/api/v1/cmms/work-orders/CANCEL-NO-SUCH/cancel", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize(Policy=perm:cmms:manage)] work-order cancel은 토큰 없이 401이어야 한다 — " +
            "인증이 대상 존재 검사보다 먼저 강제되지 않으면 비인증 쓰기가 새어 든다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (b) 비인증 게이트 — maintenance-plan cancel은 토큰 없이 401이어야 한다.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task CancelMaintenancePlan_without_auth_returns_401()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsync("/api/v1/cmms/maintenance-plans/CANCEL-NO-SUCH/cancel", null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize(Policy=perm:cmms:manage)] maintenance-plan cancel은 토큰 없이 401이어야 한다 — " +
            "인증이 대상 존재 검사보다 먼저 강제되지 않으면 비인증 쓰기가 새어 든다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (c) 작업지시 취소 해피패스 — Create(Issued) → Cancel(PUT, 204) → GET 되읽기.
    //     CMMS_WORK_ORDER UPDATE(STATUS=Cancelled) 방언 + WoRow.Restore 무손실 복원을 한 번에 증명.
    //     도메인상 Cancel은 Issued에서 곧바로 허용되므로 중간 Start가 필요 없다.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task WorkOrder_create_then_cancel_then_read_back_is_cancelled()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:cmms:manage 통과
        const string equipmentId = "EQ-CANCEL-WO-1";
        const string woId = "CANCEL-WO-1";

        // 0) 설비 선등록(기존 CMMS 파일 컨벤션) — EQUIPMENT_ID 부모.
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "Cancel WO Equipment",
            plantId = "P-CANCEL",
            areaId = "A-CANCEL",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-CANCEL"
        });
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다");

        // 1) 작업지시 생성 — CreateWorkOrderRequest(WoId, EquipmentId, WoType, Description, AssigneeId).
        //    WoType은 'PM'|'CM'만 유효. 생성 직후 상태는 Issued.
        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/work-orders", new
        {
            woId,
            equipmentId,
            woType = "CM",
            description = "Corrective work order to be cancelled",
            assigneeId = "USER-CANCEL-WO-1"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_WORK_ORDER INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<WorkOrderDto>();
        created.Should().NotBeNull();
        created!.Status.Should().Be("Issued", "생성 직후 상태는 Issued여야 한다");

        // 2) 취소 전이(PUT) — 서비스: GetById → WorkOrder.Cancel → UPDATE(STATUS=Cancelled). 성공 시 204.
        var cancelResp = await client.PutAsync($"/api/v1/cmms/work-orders/{woId}/cancel", null);
        var cancelBody = await cancelResp.Content.ReadAsStringAsync();
        cancelResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"작업지시 취소 전이(UPDATE STATUS=Cancelled)가 성공해 204여야 한다. 응답 본문: {cancelBody}");

        // 3) 설비별 되읽기(CMMS_WORK_ORDER SELECT) — 영속된 최종 상태 확인.
        //    Restore가 STATUS를 무손실 복원하지 못하면 Issued로 드러나 본 단언이 실패한다.
        var list = await client.GetFromJsonAsync<List<WorkOrderDto>>(
            $"/api/v1/cmms/work-orders?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(w => w.Id == woId)
            .Which.Status.Should().Be("Cancelled",
                "되읽은 작업지시 상태는 전이 결과(Cancelled)여야 한다 — UPDATE 영속 + Restore 무손실 복원이 둘 다 성립해야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (d) 정비계획 취소 해피패스 — Create(Planned) → Cancel(PUT, 204) → GET 되읽기.
    //     CMMS_MAINTENANCE_PLAN UPDATE(STATUS=Cancelled) 방언 + PlanRow.Restore 무손실 복원을 증명.
    //     도메인상 Cancel은 Planned에서 곧바로 허용되므로 중간 Start가 필요 없다.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MaintenancePlan_create_then_cancel_then_read_back_is_cancelled()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string equipmentId = "EQ-CANCEL-PLAN-1";
        const string planId = "CANCEL-PLAN-1";

        // 1) 정비계획 생성 — CreateMaintenancePlanRequest(PlanId, PlanName, EquipmentId, PlanType, CycleType,
        //    ScheduledDate, EstimatedDurationHours, AssigneeId). PlanType∈{PM,CM}, CycleType∈{Daily,Weekly,Monthly,Yearly}.
        //    생성 직후 상태는 Planned.
        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/maintenance-plans", new
        {
            planId,
            planName = "Plan to be cancelled",
            equipmentId,
            planType = "PM",
            cycleType = "Monthly",
            scheduledDate = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc),
            estimatedDurationHours = 2.0m,
            assigneeId = "USER-CANCEL-PLAN-1"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_MAINTENANCE_PLAN INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<MaintenancePlanDto>();
        created.Should().NotBeNull();
        created!.Status.Should().Be("Planned", "생성 직후 상태는 Planned여야 한다");

        // 2) 취소 전이(PUT) — 서비스: GetById → MaintenancePlan.Cancel → UPDATE(STATUS=Cancelled). 성공 시 204.
        var cancelResp = await client.PutAsync($"/api/v1/cmms/maintenance-plans/{planId}/cancel", null);
        var cancelBody = await cancelResp.Content.ReadAsStringAsync();
        cancelResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"정비계획 취소 전이(UPDATE STATUS=Cancelled)가 성공해 204여야 한다. 응답 본문: {cancelBody}");

        // 3) 설비별 되읽기(SELECT * WHERE EQUIPMENT_ID = @ ORDER BY SCHEDULED_DATE) — 영속된 최종 상태 확인.
        //    Restore가 STATUS를 무손실 복원하지 못하면 Planned로 드러나 본 단언이 실패한다.
        var list = await client.GetFromJsonAsync<List<MaintenancePlanDto>>(
            $"/api/v1/cmms/maintenance-plans?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == planId)
            .Which.Status.Should().Be("Cancelled",
                "되읽은 정비계획 상태는 전이 결과(Cancelled)여야 한다 — UPDATE 영속 + Restore 무손실 복원이 둘 다 성립해야 한다");
    }

    // 도메인 직렬화(JSON camelCase, JsonStringEnumConverter로 enum은 문자열). 필요한 필드만 매핑.
    private sealed record WorkOrderDto(string Id, string EquipmentId, string Status);
    private sealed record MaintenancePlanDto(string Id, string EquipmentId, string Status);
}
