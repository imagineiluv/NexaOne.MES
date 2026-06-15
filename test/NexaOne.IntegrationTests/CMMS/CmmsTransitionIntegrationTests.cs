using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.CMMS;

/// <summary>
/// CMMS(보전) 모듈 상태전이 HTTP 통합 테스트 — 기존 CMMSControllerIntegrationTests(생성/조회 스모크 +
/// work-order start 1건)가 다루지 않은 "쓰기 후 상태전이 → 되읽기 영속/방언" 경로를 SQLite 하니스에서 실증한다.
///
/// 검증 대상(미검증 쓰기/상태전이 경로):
///   - maintenance-plans/{id}/start, /complete  : CMMS_MAINTENANCE_PLAN UPDATE 방언 + 읽기경로 상태복원
///   - work-orders/{id}/complete (Remark 본문)   : CMMS_WORK_ORDER UPDATE(REMARK/COMPLETED_AT) + 상태복원
///   - spare-parts/{id}/adjust-stock (+/- 델타)  : CMMS_SPARE_PART UPDATE(CURRENT_STOCK) + 재고 되읽기
///
/// 핵심 가치:
///   (1) UPDATE SQL이 SQLite 방언에서 실제로 0행 아닌 행을 갱신하고 GET으로 영속이 확인되는지.
///   (2) 읽기경로(Repository.ToDomain) 상태손실 회귀 — 특히 MaintenancePlanRepository.PlanRow.ToDomain은
///       WorkOrder.Restore 같은 무검증 복원이 없고 Start()/Complete()/Cancel() 도메인 전이를 "재생"해
///       Status를 재구성한다. 이 재생 경로가 Completed(=Start→Complete 재생)에서 실제로 Completed로
///       복원되는지(중간 전이 실패로 Planned/InProgress에 멈추지 않는지) 되읽기로 직접 검증한다.
///   (3) 비인증 401 게이트(상태전이 엔드포인트는 [Authorize(Policy="perm:cmms:manage")]).
///
/// FK(EQUIPMENT_ID→MDM_EQUIPMENT, ASSIGNEE_ID→SYS_USER)는 하니스에서 OFF(Foreign Keys=False)라
/// 부모 시드 없이 성립한다. work-order는 기존 파일 컨벤션을 따라 설비를 선등록한다(검증 안정성).
///
/// 새 파일(충돌 방지) — 기존 CMMSControllerIntegrationTests.cs는 수정하지 않는다.
/// </summary>
public sealed class CmmsTransitionIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public CmmsTransitionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ──────────────────────────────────────────────────────────────────────────
    // (a) 비인증 게이트 — 상태전이 엔드포인트는 토큰 없이 401이어야 한다.
    //     adjust-stock(PUT, [Authorize] + perm:cmms:manage)를 대표로 검증한다.
    //     (대상 행이 없어도 인증 미들웨어가 먼저 401을 반환하므로 시드 불필요.)
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task AdjustStock_without_auth_returns_401()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PutAsJsonAsync(
            "/api/v1/cmms/spare-parts/ANY/adjust-stock", new { delta = 1m });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize(Policy=perm:cmms:manage)] adjust-stock는 토큰 없이 401이어야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (b) 정비계획 전이 해피패스 — Create → Start(Planned→InProgress) → Complete(InProgress→Completed)
    //     → GET 되읽기. CMMS_MAINTENANCE_PLAN UPDATE 방언 + 읽기경로 상태재생(ToDomain) 회귀를 한 번에 증명.
    //
    //     중요: MaintenancePlanRepository.PlanRow.ToDomain은 DB STATUS를 Restore 팩토리로 직접 복원한다
    //     (ADR-002 outbox 확산 시 Create+전이재생 → Restore로 교정 — 재생은 전이 이벤트를 재발행해 읽기경로를
    //     오염시켰다). 되읽은 status가 정확히 "Completed"인지 단언해 복원 상태손실을 잡는다.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task MaintenancePlan_create_start_complete_then_read_back_is_completed()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:cmms:manage 통과
        const string equipmentId = "EQ-CMMS-TR-PLAN-1";
        const string planId = "CMMS-PLAN-TR-1";

        // 1) 생성 — CreateMaintenancePlanRequest(PlanId, PlanName, EquipmentId, PlanType, CycleType,
        //    ScheduledDate, EstimatedDurationHours, AssigneeId). PlanType∈{PM,CM}, CycleType∈{Daily,Weekly,Monthly,Yearly}.
        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/maintenance-plans", new
        {
            planId,
            planName = "Transition Plan",
            equipmentId,
            planType = "PM",
            cycleType = "Monthly",
            scheduledDate = new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc),
            estimatedDurationHours = 3.0m,
            assigneeId = "USER-CMMS-TR-1"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_MAINTENANCE_PLAN INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<MaintenancePlanDto>();
        created.Should().NotBeNull();
        created!.Status.Should().Be("Planned", "생성 직후 상태는 Planned여야 한다");

        // 2) Start(Planned→InProgress) — CMMS_MAINTENANCE_PLAN UPDATE STATUS.
        var startResp = await client.PutAsync($"/api/v1/cmms/maintenance-plans/{planId}/start", null);
        var startBody = await startResp.Content.ReadAsStringAsync();
        startResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"정비계획 시작 전이(UPDATE)가 성공해 204여야 한다. 응답 본문: {startBody}");

        // 3) Complete(InProgress→Completed) — 두 번째 UPDATE.
        var completeResp = await client.PutAsync($"/api/v1/cmms/maintenance-plans/{planId}/complete", null);
        var completeBody = await completeResp.Content.ReadAsStringAsync();
        completeResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"정비계획 완료 전이(UPDATE)가 성공해 204여야 한다. 응답 본문: {completeBody}");

        // 4) 되읽기(SELECT * WHERE EQUIPMENT_ID = @ ORDER BY SCHEDULED_DATE) — 영속된 최종 상태 확인.
        //    ToDomain이 Completed를 재생 복원하지 못하면 여기서 Planned/InProgress로 드러난다.
        var list = await client.GetFromJsonAsync<List<MaintenancePlanDto>>(
            $"/api/v1/cmms/maintenance-plans?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == planId)
            .Which.Status.Should().Be("Completed",
                "되읽은 정비계획 상태는 전이 결과(Completed)여야 한다 — UPDATE 영속 + ToDomain 상태재생이 손실 없어야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (c) 작업지시 완료 해피패스 — Create → Start → Complete(Remark 본문) → GET 되읽기.
    //     CMMS_WORK_ORDER UPDATE(STATUS/COMPLETED_AT/REMARK) 방언 + WoRow.Restore 무손실 복원을 검증.
    //     CompleteWorkOrderRequest(Remark) — 컨트롤러가 req.Remark를 CompleteWorkOrderAsync로 전달.
    //     Complete는 InProgress에서만 허용되므로 Start 선행이 필요하다.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task WorkOrder_complete_with_remark_then_read_back_is_completed()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string equipmentId = "EQ-CMMS-TR-WO-1";
        const string woId = "CMMS-WO-TR-1";

        // 0) 설비 선등록(기존 파일 컨벤션) — EQUIPMENT_ID 부모.
        var equipResp = await client.PostAsJsonAsync("/api/v1/mdm/equipment", new
        {
            equipmentId,
            equipmentName = "Transition WO Equipment",
            plantId = "P-CMMS-TR",
            areaId = "A-CMMS-TR",
            equipmentType = "GENERIC",
            equipmentClassId = "CLS-CMMS-TR"
        });
        equipResp.StatusCode.Should().Be(HttpStatusCode.OK,
            "설비 등록(MDM_EQUIPMENT INSERT)이 성공해야 한다");

        // 1) 작업지시 생성 — CreateWorkOrderRequest(WoId, EquipmentId, WoType, Description, AssigneeId).
        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/work-orders", new
        {
            woId,
            equipmentId,
            woType = "CM",
            description = "Corrective maintenance work order",
            assigneeId = "USER-CMMS-TR-WO-1"
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_WORK_ORDER INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        // 2) Start(Issued→InProgress) — Complete의 선행 조건.
        var startResp = await client.PutAsync($"/api/v1/cmms/work-orders/{woId}/start", null);
        var startBody = await startResp.Content.ReadAsStringAsync();
        startResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"작업지시 시작 전이가 성공해 204여야 한다. 응답 본문: {startBody}");

        // 3) Complete(InProgress→Completed) — CompleteWorkOrderRequest(Remark) 본문.
        const string remark = "Replaced worn bearing; resumed operation.";
        var completeResp = await client.PutAsJsonAsync(
            $"/api/v1/cmms/work-orders/{woId}/complete", new { remark });
        var completeBody = await completeResp.Content.ReadAsStringAsync();
        completeResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"작업지시 완료 전이(UPDATE STATUS/COMPLETED_AT/REMARK)가 성공해 204여야 한다. 응답 본문: {completeBody}");

        // 4) 되읽기 — 전이 결과(Completed)와 Remark가 영속되었는지 확인.
        //    WoRow.Restore가 Status를 무손실 복원하므로 Completed로 돌아와야 한다(Issued로 유실 금지).
        var list = await client.GetFromJsonAsync<List<WorkOrderDto>>(
            $"/api/v1/cmms/work-orders?equipmentId={equipmentId}");
        list.Should().NotBeNull();
        var wo = list!.Should().ContainSingle(w => w.Id == woId).Which;
        wo.Status.Should().Be("Completed",
            "되읽은 작업지시 상태는 전이 결과(Completed)여야 한다 — UPDATE 영속 + Restore 무손실");
        wo.Remark.Should().Be(remark,
            "완료 시 본문으로 전달한 Remark가 REMARK 컬럼에 영속되어 되읽혀야 한다");
    }

    // ──────────────────────────────────────────────────────────────────────────
    // (d) 예비품 재고조정 해피패스 — Create → AdjustStock(+델타) → AdjustStock(-델타) → GET 재고 재확인.
    //     CMMS_SPARE_PART UPDATE(CURRENT_STOCK) 방언 + DECIMAL 직렬화/영속을 증명한다.
    //     AdjustStockRequest(Delta) — 양/음 델타 누적이 정확히 반영되는지(10 +5 -3 = 12) 되읽기로 검증.
    // ──────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task SparePart_adjust_stock_positive_and_negative_persists_running_total()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string partId = "CMMS-PART-TR-1";

        // 1) 예비품 생성 — 초기 재고 10. CreateSparePartRequest(PartId, PartName, PartNumber, Description,
        //    UnitOfMeasure, CurrentStock, MinStock, MaxStock, Location, EquipmentClassId).
        var createResp = await client.PostAsJsonAsync("/api/v1/cmms/spare-parts", new
        {
            partId,
            partName = "Transition Bearing",
            partNumber = "BRG-TR-001",
            description = "Stock adjustment test part",
            unitOfMeasure = "EA",
            currentStock = 10m,
            minStock = 2m,
            maxStock = 100m,
            location = "WH-TR-01",
            equipmentClassId = (string?)null
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"CMMS_SPARE_PART INSERT가 성공해 200이어야 한다. 응답 본문: {createBody}");

        // 2) +5 조정 — UPDATE CURRENT_STOCK. 10 → 15.
        var addResp = await client.PutAsJsonAsync(
            $"/api/v1/cmms/spare-parts/{partId}/adjust-stock", new { delta = 5m });
        var addBody = await addResp.Content.ReadAsStringAsync();
        addResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"양(+)의 재고조정 UPDATE가 성공해 204여야 한다. 응답 본문: {addBody}");

        // 3) -3 조정 — 두 번째 UPDATE. 15 → 12. (음의 델타가 0 이상 유지되어 도메인 가드 통과)
        var subResp = await client.PutAsJsonAsync(
            $"/api/v1/cmms/spare-parts/{partId}/adjust-stock", new { delta = -3m });
        var subBody = await subResp.Content.ReadAsStringAsync();
        subResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"음(-)의 재고조정 UPDATE가 성공해 204여야 한다. 응답 본문: {subBody}");

        // 4) 되읽기(SELECT * ORDER BY PART_NAME) — 누적 재고(10+5-3=12)가 영속되었는지 확인.
        var list = await client.GetFromJsonAsync<List<SparePartDto>>("/api/v1/cmms/spare-parts");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == partId)
            .Which.CurrentStock.Should().Be(12m,
                "되읽은 재고는 양·음 델타 누적(10+5-3=12)이어야 한다 — UPDATE CURRENT_STOCK 영속 + DECIMAL 정합");
    }

    // 도메인 직렬화(JSON camelCase, JsonStringEnumConverter로 enum은 문자열). 필요한 필드만 매핑.
    private sealed record WorkOrderDto(string Id, string EquipmentId, string WoType, string Status, string? Remark);
    private sealed record MaintenancePlanDto(string Id, string EquipmentId, string Status);
    private sealed record SparePartDto(string Id, string PartNumber, decimal CurrentStock);
}
