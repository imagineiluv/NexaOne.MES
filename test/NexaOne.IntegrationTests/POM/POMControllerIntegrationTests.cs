using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.POM;

/// <summary>
/// POM 생산계획 관리 HTTP 통합 테스트 — V007 마이그레이션으로 생성되는 POM_PRODUCTION_PLAN 테이블이
/// SQLite에서 실제로 동작하는지(방언 SELECT/INSERT + 인증 + 라우팅 + 직렬화)를 end-to-end로 검증한다.
///
/// 읽기 스모크: GET /api/v1/pom/plans?plantId=... 는 미인증 401, 인증 200(빈 목록 허용)을 보장한다.
///   - PomController.GetPlans 는 [FromQuery] string plantId 가 필수이며 from/to 는 nullable 선택값이다.
///   - GetByPlantAsync 는 'PLANT_ID = @plantId AND (@from IS NULL OR ...) AND (@to IS NULL OR ...)' 동등 조회라
///     존재하지 않는 plantId 라도 시드 없이 항상 200/빈배열을 반환한다(읽기 전용, 무조건 통과).
///
/// 쓰기 해피패스: POST /api/v1/pom/plans 로 생산계획(주 엔티티) 1건을 생성한다.
///   - POM_PRODUCTION_PLAN 에는 FOREIGN KEY 제약이 없다(PLANT_ID/PRODUCT_ID 는 단순 컬럼).
///     따라서 FK 부모(MDM equipment/plant) 선행 시드가 불필요하다.
///   - 감사 컬럼(CREATED_BY/CREATED_AT/UPDATED_BY/UPDATED_AT, 모두 NOT NULL)은 ServiceObjectProcessor.InjectAudit 가
///     INSERT 시점에 비-null 값으로 채우므로 NOT NULL 위반이 없다.
///   - 생성 후 같은 plantId 로 목록을 되읽어 영속화를 확인한다(enum Status 미개입의 단순 동등 필터).
///
/// orders 검증: V028 마이그레이션이 POM_PRODUCTION_ORDER 테이블을 생성하므로 /api/v1/pom/orders 계열을 검증한다.
///   - GetOrders 는 [FromQuery] string planId 가 필수이며 'PLAN_ID = @planId ORDER BY SCHEDULED_START' 동등 조회라
///     존재하지 않는 planId 라도 시드 없이 항상 200/빈배열을 반환한다(읽기 전용, 무조건 통과).
///   - 쓰기 해피패스: POST /api/v1/pom/orders 는 PLAN_ID(POM_PRODUCTION_PLAN)·EQUIPMENT_ID(MDM_EQUIPMENT)에 FK가
///     있으나 테스트 하네스는 FK OFF 로 동작하므로 부모 시드 없이 INSERT 가 통과한다(감사 컬럼은 InjectAudit 가 채움).
/// </summary>
public sealed class POMControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public POMControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetPlans_requires_auth_and_returns_ok_when_authenticated()
    {
        const string plantId = "P-POM";

        // 미인증 클라이언트 → 401 (라우팅·인증 파이프라인 검증, 시드 불필요)
        var anonymous = _factory.CreateClient();
        var anonResp = await anonymous.GetAsync($"/api/v1/pom/plans?plantId={plantId}");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 요청은 [Authorize]로 401이어야 한다");

        // 인증 클라이언트 → 200 + 빈/JSON 배열 (POM_PRODUCTION_PLAN 테이블 존재 + SQLite SELECT + 직렬화 검증)
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN)
        var resp = await client.GetAsync($"/api/v1/pom/plans?plantId={plantId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"POM_PRODUCTION_PLAN 조회(SELECT)가 성공해야 한다. 응답 본문: {body}");

        var plans = await resp.Content.ReadFromJsonAsync<List<PlanDto>>();
        plans.Should().NotBeNull("목록 엔드포인트는 JSON 배열을 반환해야 한다");
    }

    [Fact]
    public async Task CreatePlan_persists_and_is_retrievable_by_plant()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:pom:manage 정책 통과
        const string planId = "POM-IT-1";
        const string plantId = "P-POM";
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc);

        // 1) 생산계획 생성 — FK 부모 불필요(POM_PRODUCTION_PLAN 에 FK 없음). 본문 필드는 CreatePlanRequest 레코드와 1:1.
        var createResp = await client.PostAsJsonAsync("/api/v1/pom/plans", new
        {
            planId,
            planName = "POM Integration Test Plan",
            plantId,
            productId = "PROD-POM",
            qty = 100m,
            startDate = start,
            endDate = end
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"생산계획 생성(POM_PRODUCTION_PLAN INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<PlanDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(planId, "생성된 계획의 식별자는 요청한 PLAN_ID여야 한다");
        created.Status.Should().Be("Draft", "신규 생산계획의 초기 상태는 Draft여야 한다");

        // 2) 동일 plantId로 목록 조회(PLANT_ID 동등 필터, from/to 미지정) — 영속화 확인.
        var list = await client.GetFromJsonAsync<List<PlanDto>>($"/api/v1/pom/plans?plantId={plantId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(p => p.Id == planId,
            "생성한 계획이 동일 plant 목록에 정확히 1건 조회되어야 한다");
    }

    [Fact]
    public async Task GetOrders_requires_auth_and_returns_ok_when_authenticated()
    {
        const string planId = "POM-ORD-PLAN";

        // 미인증 클라이언트 → 401 (라우팅·인증 파이프라인 검증, 시드 불필요)
        var anonymous = _factory.CreateClient();
        var anonResp = await anonymous.GetAsync($"/api/v1/pom/orders?planId={planId}");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 요청은 [Authorize]로 401이어야 한다");

        // 인증 클라이언트 → 200 + 빈/JSON 배열 (POM_PRODUCTION_ORDER 테이블 존재 + SQLite SELECT + 직렬화 검증)
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN)
        var resp = await client.GetAsync($"/api/v1/pom/orders?planId={planId}");
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"POM_PRODUCTION_ORDER 조회(SELECT)가 성공해야 한다. 응답 본문: {body}");

        var orders = await resp.Content.ReadFromJsonAsync<List<OrderDto>>();
        orders.Should().NotBeNull("목록 엔드포인트는 JSON 배열을 반환해야 한다");
    }

    [Fact]
    public async Task CreateOrder_persists_and_is_retrievable_by_plan()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:pom:manage 정책 통과
        const string orderId = "POM-ORD-IT-1";
        const string planId = "POM-ORD-IT-PLAN";
        var start = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 2, 28, 0, 0, 0, DateTimeKind.Utc);

        // 1) 생산지시 생성 — 하네스 FK OFF 이므로 PLAN_ID/EQUIPMENT_ID 부모 시드 불필요.
        //    본문 필드는 CreateProductionOrderRequest 레코드와 1:1. 감사 컬럼은 InjectAudit 가 채운다.
        var createResp = await client.PostAsJsonAsync("/api/v1/pom/orders", new
        {
            orderId,
            planId,
            equipmentId = "EQP-POM-ORD",
            productId = "PROD-POM-ORD",
            orderQty = 50m,
            scheduledStart = start,
            scheduledEnd = end
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"생산지시 생성(POM_PRODUCTION_ORDER INSERT)이 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<OrderDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(orderId, "생성된 지시의 식별자는 요청한 ORDER_ID여야 한다");
        created.Status.Should().Be("Issued", "신규 생산지시의 초기 상태는 Issued여야 한다");

        // 2) 동일 planId로 목록 조회(PLAN_ID 동등 필터) — 영속화 + SELECT 컬럼 정합 확인.
        var list = await client.GetFromJsonAsync<List<OrderDto>>($"/api/v1/pom/orders?planId={planId}");
        list.Should().NotBeNull();
        list!.Should().ContainSingle(o => o.Id == orderId,
            "생성한 지시가 동일 plan 목록에 정확히 1건 조회되어야 한다");
    }

    // 응답 본문은 POM 도메인 ProductionPlan(camelCase, Status enum은 JsonStringEnumConverter로 문자열).
    private sealed record PlanDto(string Id, string PlanName, string PlantId, string ProductId, string Status);

    // 응답 본문은 POM 도메인 ProductionOrder(camelCase, Status enum은 JsonStringEnumConverter로 문자열).
    private sealed record OrderDto(string Id, string PlanId, string EquipmentId, string ProductId, string Status);
}
