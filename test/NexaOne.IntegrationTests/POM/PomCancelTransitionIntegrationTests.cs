using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.POM;

/// <summary>
/// POM "취소(Cancel)" 상태전이 HTTP 통합 테스트 — 두 보안 전이 엔드포인트
/// (PUT plans/{id}/cancel, PUT orders/{id}/cancel)를 생성(POST)→취소(PUT)→되읽기(GET) 왕복으로 검증한다.
/// 기존 PomTransitionIntegrationTests는 release/start/complete만 다루므로 cancel 전이는 통합 커버리지가 없었다.
///
/// 도메인 사실(직접 확인):
///   - ProductionPlan.Cancel(): Completed/Cancelled가 아니면 어떤 상태에서도 가능 → 신규 Draft에서 즉시 취소 가능.
///   - ProductionOrder.Cancel(): Completed/Cancelled가 아니면 가능 → 신규 Issued에서 즉시 취소 가능.
///   - Status enum은 둘 다 'Cancelled' 멤버를 가지며 JsonStringEnumConverter로 "Cancelled" 문자열로 직렬화된다.
///   - 두 리포지토리의 ToDomain()은 Restore(...)로 영속 STATUS를 직접 복원하므로 취소 상태가 읽기 경로에서 보존된다.
///
/// 전이 엔드포인트는 성공 시 204 NoContent(컨트롤러: result.IsSuccess ? NoContent() : BadRequest)를 반환하므로,
/// 204 자체가 GetByIdAsync(SELECT) → Cancel() → UpdateAsync(UPDATE) 경로가 SQLite에서 동작했음을 입증한다.
/// 그 다음 목록 GET으로 되읽어 STATUS=Cancelled가 영속·재구성되는지 확인한다(읽기 경로 상태 손실 탐지).
///
/// 하네스 사실: TestApiFactory는 클래스별 고유 SQLite 임시 DB + FK OFF + db/migrations 부트스트랩.
///   - 전이 대상 행(plan/order)을 테스트가 먼저 생성하므로 FK-OFF와 무관하게 전이가 성립한다.
///   - POM_PRODUCTION_PLAN에는 FK가 없고, POM_PRODUCTION_ORDER의 PLAN_ID/EQUIPMENT_ID FK는 하네스 FK OFF로 무시된다(부모 시드 불필요).
///   - 감사 컬럼(CREATED_BY/AT, UPDATED_BY/AT, 모두 NOT NULL)은 ServiceObjectProcessor.InjectAudit가 INSERT 시 채운다.
///   - CreateAuthenticatedClient()는 기본 "*"(ADMIN, sub="test-admin")라 [Authorize(Policy="perm:pom:manage")]를 통과한다.
///   - 기존 POM 테스트와 충돌하지 않도록 'CXL' 고유 ID 공간을 쓴다.
/// </summary>
public sealed class PomCancelTransitionIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public PomCancelTransitionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── 미인증 취소 차단 가드(2종) ───────────────────────────────────────────────────

    [Fact]
    public async Task Cancel_endpoints_require_auth()
    {
        // 토큰 없는 PUT cancel 요청은 정책 평가 전 인증 단계에서 401이어야 한다(라우팅·인증 파이프라인 검증).
        // 대상 행이 없어도 인증이 먼저 평가되므로 401이 기대값이며 시드가 불필요하다.
        var anon = _factory.CreateClient();

        var planResp = await anon.PutAsync("/api/v1/pom/plans/POM-CXL-AUTH-NONE/cancel", null);
        planResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 plan 취소(PUT cancel)는 [Authorize]로 401이어야 한다");

        var orderResp = await anon.PutAsync("/api/v1/pom/orders/POM-CXL-ORD-AUTH-NONE/cancel", null);
        orderResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 order 취소(PUT cancel)는 [Authorize]로 401이어야 한다");
    }

    // ── 생산계획 취소 해피패스: Draft → Cancelled ────────────────────────────────────

    [Fact]
    public async Task Plan_cancel_from_draft_persists_cancelled_status_through_read_path()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:pom:manage 통과
        const string planId = "POM-CXL-PLAN-1";
        const string plantId = "P-POM-CXL";
        var start = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 5, 31, 0, 0, 0, DateTimeKind.Utc);

        // 1) 생성(POST plans → POM_PRODUCTION_PLAN INSERT). 본문은 CreatePlanRequest 레코드와 1:1. 초기 상태 Draft.
        var createResp = await client.PostAsJsonAsync("/api/v1/pom/plans", new
        {
            planId,
            planName = "POM Cancel Plan",
            plantId,
            productId = "PROD-POM-CXL",
            qty = 150m,
            startDate = start,
            endDate = end
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"생산계획 생성(INSERT)이 성공해야 한다. 응답 본문: {createBody}");
        var created = await createResp.Content.ReadFromJsonAsync<PlanDto>();
        created!.Status.Should().Be("Draft", "신규 생산계획의 초기 상태는 Draft여야 한다");

        // 2) Cancel(PUT plans/{id}/cancel) — Draft→Cancelled. 204는 SELECT→Cancel()→UPDATE가 SQLite에서 성공했음을 의미.
        var cancelResp = await client.PutAsync($"/api/v1/pom/plans/{planId}/cancel", null);
        var cancelBody = await cancelResp.Content.ReadAsStringAsync();
        cancelResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Cancel(Draft→Cancelled) 전이가 성공(204)해야 한다. 응답 본문: {cancelBody}");

        // 3) 되읽기(GET plans?plantId=…) — 취소 결과가 STATUS 컬럼에 영속·재구성됐는지 확인(읽기 경로 상태 손실 탐지).
        //    PlanRow.ToDomain()은 Restore(...)로 영속 STATUS를 복원하므로 'Cancelled'가 보존돼야 한다.
        var list = await client.GetFromJsonAsync<List<PlanDto>>($"/api/v1/pom/plans?plantId={plantId}");
        list.Should().NotBeNull();
        var roundTripped = list!.Should().ContainSingle(p => p.Id == planId,
            "취소한 계획이 동일 plant 목록에 정확히 1건 조회돼야 한다(영속화 확인)").Which;
        roundTripped.Status.Should().Be("Cancelled",
            "Cancel 후 STATUS가 'Cancelled'로 영속·재구성돼야 한다 — Restore가 영속 상태를 복원하므로 읽기 경로 상태 손실이 없어야 한다");
    }

    // ── 생산지시 취소 해피패스: Issued → Cancelled ───────────────────────────────────

    [Fact]
    public async Task Order_cancel_from_issued_persists_cancelled_status_through_read_path()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:pom:manage 통과
        const string orderId = "POM-CXL-ORD-1";
        const string planId = "POM-CXL-ORD-PLAN";
        var start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 6, 30, 0, 0, 0, DateTimeKind.Utc);

        // 1) 생성(POST orders → POM_PRODUCTION_ORDER INSERT). 본문은 CreateProductionOrderRequest 레코드와 1:1. 초기 Issued.
        //    하네스 FK OFF이므로 PLAN_ID/EQUIPMENT_ID 부모 시드 불필요. 감사 컬럼은 InjectAudit가 채운다.
        var createResp = await client.PostAsJsonAsync("/api/v1/pom/orders", new
        {
            orderId,
            planId,
            equipmentId = "EQP-POM-CXL",
            productId = "PROD-POM-CXL-ORD",
            orderQty = 80m,
            scheduledStart = start,
            scheduledEnd = end
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"생산지시 생성(INSERT)이 성공해야 한다. 응답 본문: {createBody}");
        var created = await createResp.Content.ReadFromJsonAsync<OrderDto>();
        created!.Status.Should().Be("Issued", "신규 생산지시의 초기 상태는 Issued여야 한다");

        // 2) Cancel(PUT orders/{id}/cancel) — Issued→Cancelled. 204는 SELECT→Cancel()→UPDATE가 SQLite에서 성공했음을 의미.
        var cancelResp = await client.PutAsync($"/api/v1/pom/orders/{orderId}/cancel", null);
        var cancelBody = await cancelResp.Content.ReadAsStringAsync();
        cancelResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Cancel(Issued→Cancelled) 전이가 성공(204)해야 한다. 응답 본문: {cancelBody}");

        // 3) 되읽기(GET orders?planId=…) — STATUS=Cancelled 영속·재구성 확인.
        //    OrderRow.ToDomain()은 Restore(...)로 영속 STATUS를 복원하므로 'Cancelled'가 보존돼야 한다.
        var list = await client.GetFromJsonAsync<List<OrderDto>>($"/api/v1/pom/orders?planId={planId}");
        list.Should().NotBeNull();
        var roundTripped = list!.Should().ContainSingle(o => o.Id == orderId,
            "취소한 지시가 동일 plan 목록에 정확히 1건 조회돼야 한다(영속화 확인)").Which;
        roundTripped.Status.Should().Be("Cancelled",
            "Cancel 후 STATUS가 'Cancelled'로 영속·재구성돼야 한다 — Restore가 영속 상태를 복원하므로 읽기 경로 상태 손실이 없어야 한다");
    }

    // ── 응답 DTO (JSON camelCase, Status enum은 JsonStringEnumConverter로 문자열) ─────────
    private sealed record PlanDto(string Id, string PlanName, string PlantId, string ProductId, string Status);

    private sealed record OrderDto(
        string Id, string PlanId, string EquipmentId, string ProductId,
        decimal OrderQty, decimal? ActualQty, string Status);
}
