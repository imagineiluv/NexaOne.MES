using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SHP;

/// <summary>
/// SHP 출하/배송 HTTP 통합 테스트 — V009 마이그레이션으로 생성되는 SHP_DELIVERY_ORDER 테이블이
/// SQLite에서 실제로 동작하는지(테이블 존재 + 방언 SELECT/INSERT + 인증 + 라우팅 + 직렬화)를
/// end-to-end로 검증한다. SHP_DELIVERY_ORDER는 FK 제약이 없어(PLANT_ID는 단순 NVARCHAR) 선행 시드가
/// 필요 없으므로 주문 생성 happy-path를 안전하게 포함한다.
/// 메인 목록 엔드포인트는 GET /api/v1/shp/orders?plantId=... 이다.
/// </summary>
public sealed class SHPControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public SHPControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task GetOrders_requires_auth_and_returns_ok_for_admin()
    {
        const string plantId = "P-SHP";

        // 1) 미인증 클라이언트는 401이어야 한다([Authorize] 컨트롤러 기본 정책).
        var anonymous = _factory.CreateClient();
        var anonResp = await anonymous.GetAsync($"/api/v1/shp/orders?plantId={plantId}");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 요청은 [Authorize]에 의해 401이어야 한다");

        // 2) 인증 클라이언트("*" ADMIN)는 200 + (비어 있어도 무방한) 리스트를 받아야 한다.
        //    이 호출 자체가 SHP_DELIVERY_ORDER 테이블 존재 + SQLite SELECT 방언 + 직렬화를 증명한다.
        var client = _factory.CreateAuthenticatedClient();
        var okResp = await client.GetAsync($"/api/v1/shp/orders?plantId={plantId}");
        var body = await okResp.Content.ReadAsStringAsync();
        okResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ORDER 목록 조회(SELECT)가 성공해야 한다. 응답 본문: {body}");

        var orders = await okResp.Content.ReadFromJsonAsync<List<OrderDto>>();
        orders.Should().NotBeNull("성공 응답은 JSON 배열(빈 배열 포함)이어야 한다");
    }

    [Fact]
    public async Task CreateOrder_persists_and_is_returned_by_get()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — perm:shp:manage 정책 통과
        const string plantId = "P-SHP-W";
        const string orderId = "SHP-IT-1";
        var requestedDate = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);

        // 1) 주문 생성(POST /api/v1/shp/orders). FK 부모 없음 — PLANT_ID는 단순 컬럼이라 선행 시드 불필요.
        var createResp = await client.PostAsJsonAsync("/api/v1/shp/orders", new
        {
            orderId,
            customerName = "SHP Integration Customer",
            plantId,
            requestedDate
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ORDER INSERT가 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<OrderDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(orderId, "생성된 주문의 식별자(Id)는 요청한 OrderId여야 한다");
        created.Status.Should().Be("Draft", "신규 주문은 Draft 상태로 생성되어야 한다(enum→문자열 직렬화)");

        // 2) 목록 조회로 영속화 확인(SHP_DELIVERY_ORDER SELECT) — 생성한 주문이 조회돼야 한다.
        var orders = await client.GetFromJsonAsync<List<OrderDto>>($"/api/v1/shp/orders?plantId={plantId}");
        orders.Should().NotBeNull();
        orders!.Should().ContainSingle(o => o.Id == orderId)
            .Which.PlantId.Should().Be(plantId, "INSERT한 PLANT_ID가 그대로 영속화돼야 한다");
    }

    // DeliveryOrder 직렬화 형태(camelCase). Id는 OrderId(=PK), Status는 JsonStringEnumConverter로 문자열.
    private sealed record OrderDto(string Id, string CustomerName, string PlantId, DateTime RequestedDate, string Status);
}
