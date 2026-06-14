using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.POM;

/// <summary>
/// POM 생산계획/생산지시 "상태전이" HTTP 통합 테스트 — 생성(POST) → 전이(PUT) → 되읽기(GET) 시나리오로
/// UPDATE SQL이 SQLite 방언에서 실제로 동작하고 그 결과가 영속화돼 읽기 경로로 다시 보이는지(상태 손실 없음)를
/// end-to-end로 검증한다. 기존 POMControllerIntegrationTests(GET/POST만)와 충돌하지 않도록 별도 ID 공간을 쓴다.
///
/// 도메인 상태머신(주의: 컨트롤러 라우트 이름 순서 ≠ 전이 순서):
///   - ProductionPlan: Draft ──Release──▶ Released ──Start──▶ InProgress ──Complete──▶ Completed
///       · Release()는 Draft에서만, Start()는 Released에서만, Complete()는 InProgress에서만 허용된다.
///       · 따라서 유효 해피패스는 release → start → complete 순이다(컨트롤러 라우트는 start/release/complete 순서이나
///         도메인 가드가 실제 순서를 강제한다). Cancel은 Completed/Cancelled가 아니면 언제든 가능.
///   - ProductionOrder: Issued ──Start──▶ InProgress ──Complete(actualQty)──▶ Completed
///       · Complete는 InProgress에서만 허용되며 ActualQty/ActualEnd를 기록한다.
///
/// 전이 엔드포인트는 성공 시 204 NoContent(컨트롤러: result.IsSuccess ? NoContent() : BadRequest)를 반환하므로,
/// 204 자체가 GetByIdAsync(SELECT) → 도메인 전이 → UpdateAsync(UPDATE) 경로가 SQLite에서 동작했음을 입증한다.
/// 그 다음 목록 GET으로 되읽어 STATUS가 최종 상태로 영속·재구성되는지 확인한다(읽기 경로 상태 손실 탐지).
///
/// 하네스 사실: TestApiFactory는 클래스별 고유 SQLite 임시 DB + FK OFF + db/migrations 부트스트랩.
///   - 전이 대상 행(plan/order)을 테스트가 먼저 생성하므로 FK-OFF와 무관하게 전이가 성립한다.
///   - POM_PRODUCTION_PLAN에는 FK가 없고, POM_PRODUCTION_ORDER의 PLAN_ID/EQUIPMENT_ID FK는 하네스 FK OFF로 무시된다.
///   - CreateAuthenticatedClient()는 기본 "*"(ADMIN, sub="test-admin")라 [Authorize(Policy="perm:pom:manage")]를 통과한다.
/// </summary>
public sealed class PomTransitionIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public PomTransitionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 미인증 전이 차단 스모크 ────────────────────────────────────────────────────

    [Fact]
    public async Task Transition_endpoints_require_auth()
    {
        // 토큰 없는 PUT 전이 요청은 [Authorize]/정책 적용 전 인증 단계에서 401이어야 한다(라우팅·인증 파이프라인 검증).
        // 대상 행이 없어도 인증이 먼저 평가되므로 401이 기대값이며, 시드가 불필요하다.
        var anon = _factory.CreateClient();

        var planResp = await anon.PutAsync("/api/v1/pom/plans/POM-AUTH-NONE/release", null);
        planResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 plan 전이(PUT release)는 [Authorize]로 401이어야 한다");

        var orderResp = await anon.PutAsync("/api/v1/pom/orders/POM-ORD-AUTH-NONE/start", null);
        orderResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 order 전이(PUT start)는 [Authorize]로 401이어야 한다");
    }

    // ── 생산계획 전이 해피패스: Draft → Released → InProgress → Completed ──────────────

    [Fact]
    public async Task Plan_lifecycle_release_start_complete_persists_status_through_read_path()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:pom:manage 통과
        const string planId = "POM-TR-PLAN-1";
        const string plantId = "P-POM-TR";
        var start = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 31, 0, 0, 0, DateTimeKind.Utc);

        // 1) 생성(POST plans → POM_PRODUCTION_PLAN INSERT). 본문은 CreatePlanRequest 레코드와 1:1. 초기 상태 Draft.
        var createResp = await client.PostAsJsonAsync("/api/v1/pom/plans", new
        {
            planId,
            planName = "POM Transition Plan",
            plantId,
            productId = "PROD-POM-TR",
            qty = 200m,
            startDate = start,
            endDate = end
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"생산계획 생성(INSERT)이 성공해야 한다. 응답 본문: {createBody}");
        var created = await createResp.Content.ReadFromJsonAsync<PlanDto>();
        created!.Status.Should().Be("Draft", "신규 생산계획의 초기 상태는 Draft여야 한다");

        // 2) Release(PUT plans/{id}/release) — Draft→Released. 204는 SELECT→도메인전이→UPDATE가 SQLite에서 성공했음을 의미.
        var releaseResp = await client.PutAsync($"/api/v1/pom/plans/{planId}/release", null);
        var releaseBody = await releaseResp.Content.ReadAsStringAsync();
        releaseResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Release(Draft→Released) 전이가 성공(204)해야 한다. 응답 본문: {releaseBody}");

        // 3) Start(PUT plans/{id}/start) — Released→InProgress. (컨트롤러 라우트명 'start'지만 도메인은 Released 요구)
        var startResp = await client.PutAsync($"/api/v1/pom/plans/{planId}/start", null);
        var startBody = await startResp.Content.ReadAsStringAsync();
        startResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Start(Released→InProgress) 전이가 성공(204)해야 한다. 응답 본문: {startBody}");

        // 4) Complete(PUT plans/{id}/complete) — InProgress→Completed.
        var completeResp = await client.PutAsync($"/api/v1/pom/plans/{planId}/complete", null);
        var completeBody = await completeResp.Content.ReadAsStringAsync();
        completeResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Complete(InProgress→Completed) 전이가 성공(204)해야 한다. 응답 본문: {completeBody}");

        // 5) 되읽기(GET plans?plantId=…) — 전이 결과가 STATUS 컬럼에 영속·재구성됐는지 확인(읽기 경로 상태 손실 탐지).
        //    ⚠️ 읽기 경로 버그 노출 지점: ProductionPlanRepository.PlanRow.ToDomain()은
        //       ProductionPlan.Create(...)만 호출하고(Create는 항상 Status=Draft) 영속된 STATUS 컬럼을 도메인에 재적용하지 않는다.
        //       (ProductionOrderRepository.OrderRow.ToDomain()은 상태 replay로 재구성하는데, plan은 누락 → 읽기 시 항상 Draft.)
        //       따라서 이 단언은 read-path 상태 손실 버그가 고쳐지기 전까지 실패하며, 그 자체가 버그를 드러낸다.
        var list = await client.GetFromJsonAsync<List<PlanDto>>($"/api/v1/pom/plans?plantId={plantId}");
        list.Should().NotBeNull();
        var roundTripped = list!.Should().ContainSingle(p => p.Id == planId,
            "전이한 계획이 동일 plant 목록에 정확히 1건 조회돼야 한다(영속화 확인)").Which;
        roundTripped.Status.Should().Be("Completed",
            "Release→Start→Complete 후 STATUS가 'Completed'로 영속·재구성돼야 한다(읽기 경로 상태 손실 없음). " +
            "실패 시: ProductionPlanRepository.PlanRow.ToDomain()이 영속 STATUS를 무시하고 Create의 기본값 Draft만 반환하는 버그.");
    }

    // ── 생산지시 전이 해피패스: Issued → InProgress → Completed(ActualQty 본문) ─────────

    [Fact]
    public async Task Order_lifecycle_start_complete_persists_status_and_actual_qty()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:pom:manage 통과
        const string orderId = "POM-TR-ORD-1";
        const string planId = "POM-TR-ORD-PLAN";
        const decimal orderQty = 120m;
        const decimal actualQty = 118m;
        var start = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 4, 30, 0, 0, 0, DateTimeKind.Utc);

        // 1) 생성(POST orders → POM_PRODUCTION_ORDER INSERT). 본문은 CreateProductionOrderRequest 레코드와 1:1. 초기 Issued.
        //    하네스 FK OFF이므로 PLAN_ID/EQUIPMENT_ID 부모 시드 불필요. 감사 컬럼은 InjectAudit가 채운다.
        var createResp = await client.PostAsJsonAsync("/api/v1/pom/orders", new
        {
            orderId,
            planId,
            equipmentId = "EQP-POM-TR",
            productId = "PROD-POM-TR-ORD",
            orderQty,
            scheduledStart = start,
            scheduledEnd = end
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"생산지시 생성(INSERT)이 성공해야 한다. 응답 본문: {createBody}");
        var created = await createResp.Content.ReadFromJsonAsync<OrderDto>();
        created!.Status.Should().Be("Issued", "신규 생산지시의 초기 상태는 Issued여야 한다");

        // 2) Start(PUT orders/{id}/start) — Issued→InProgress. ACTUAL_START 기록 + UPDATE.
        var startResp = await client.PutAsync($"/api/v1/pom/orders/{orderId}/start", null);
        var startBody = await startResp.Content.ReadAsStringAsync();
        startResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Start(Issued→InProgress) 전이가 성공(204)해야 한다. 응답 본문: {startBody}");

        // 3) Complete(PUT orders/{id}/complete, 본문 CompleteOrderRequest{ActualQty}) — InProgress→Completed.
        //    ActualQty는 본문에서 오고, ActualEnd는 서비스가 DateTime.UtcNow로 채운다.
        var completeResp = await client.PutAsJsonAsync($"/api/v1/pom/orders/{orderId}/complete", new
        {
            actualQty
        });
        var completeBody = await completeResp.Content.ReadAsStringAsync();
        completeResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Complete(InProgress→Completed, ActualQty 본문) 전이가 성공(204)해야 한다. 응답 본문: {completeBody}");

        // 4) 되읽기(GET orders?planId=…) — STATUS=Completed + ACTUAL_QTY 영속·재구성 확인.
        //    OrderRow.ToDomain()은 영속 STATUS를 replay(Start→Complete)로 재구성하므로 읽기 경로가 Completed/ActualQty를 보존해야 한다.
        var list = await client.GetFromJsonAsync<List<OrderDto>>($"/api/v1/pom/orders?planId={planId}");
        list.Should().NotBeNull();
        var roundTripped = list!.Should().ContainSingle(o => o.Id == orderId,
            "전이한 지시가 동일 plan 목록에 정확히 1건 조회돼야 한다(영속화 확인)").Which;
        roundTripped.Status.Should().Be("Completed",
            "Start→Complete 후 STATUS가 'Completed'로 영속·재구성돼야 한다(읽기 경로 상태 손실 없음).");
        roundTripped.ActualQty.Should().Be(actualQty,
            "Complete 시 본문 ActualQty가 ACTUAL_QTY로 영속·재구성돼야 한다(decimal 방언 왕복).");
    }

    // ── 응답 DTO (JSON camelCase, Status enum은 JsonStringEnumConverter로 문자열) ─────────
    private sealed record PlanDto(string Id, string PlanName, string PlantId, string ProductId, string Status);

    private sealed record OrderDto(
        string Id, string PlanId, string EquipmentId, string ProductId,
        decimal OrderQty, decimal? ActualQty, string Status);
}
