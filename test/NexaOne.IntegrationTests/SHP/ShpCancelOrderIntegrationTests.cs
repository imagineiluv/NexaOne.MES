using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SHP;

/// <summary>
/// SHP CancelOrder 상태전이 HTTP 통합 테스트 — PUT /api/v1/shp/orders/{id}/cancel 경로가 SQLite에서
/// 실제로 동작하는지(테이블/방언 SELECT·UPDATE + 인증/인가 + 도메인 상태 가드 + 영속·되읽기)를
/// end-to-end로 실증한다. 기존 SHPControllerIntegrationTests.cs(생성/읽기)·ShpLifecycleIntegrationTests.cs
/// (Confirm/Ship)와 중복되지 않는 cancel 전이 전용 시나리오만 담는다.
///
/// 검증 시나리오:
///   (A) CreateOrder(Draft) → PUT cancel → 204 → GET orders 되읽기로 STATUS='Cancelled' 영속·복원 확인.
///       cancel은 SHP_DELIVERY_ORDER UPDATE(STATUS='Cancelled')를 트리거하며, 되읽기는 읽기경로
///       (OrderRow.ToDomain→DeliveryOrder.Restore)가 DB STATUS 컬럼을 복원함을 실증한다.
///   (B) Create→Confirm→Ship 후 cancel 시도 → 도메인 가드 위반 → 409 Conflict.
///       DeliveryOrder.Cancel()은 Status가 Shipped/Cancelled면 Error.Conflict를 반환하고,
///       ToActionResult가 409 Conflict로 매핑한다(Wave C-α).
///   (C) 미인증 cancel → 401 ([Authorize] + perm:shp:manage).
///
/// 하니스 사실(TestApiFactory): 클래스별 고유 SQLite 임시 DB(GUID) + Foreign Keys=False(FK 비강제) +
/// db/migrations(V009 SHP_DELIVERY_ORDER) 부트스트랩. CreateAuthenticatedClient()는 기본 "*"(ADMIN)로
/// 발급되어 perm:shp:manage를 통과한다. 전이 대상 주문을 먼저 POST로 생성하므로 FK-OFF와 무관하게 성립한다.
/// </summary>
public sealed class ShpCancelOrderIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public ShpCancelOrderIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── (C) 인증 가드 ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CancelOrder_requires_auth()
    {
        // 미인증 cancel PUT은 401이어야 한다([Authorize] + perm:shp:manage; 인가가 라우팅/핸들러보다 우선).
        var anonymous = _factory.CreateClient();
        var resp = await anonymous.PutAsync("/api/v1/shp/orders/ANY-ORDER/cancel", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 cancel 요청은 [Authorize]에 의해 401이어야 한다");
    }

    // ── (A) Draft → cancel → 되읽기 STATUS='Cancelled' ───────────────────────────

    [Fact]
    public async Task CreateOrder_then_Cancel_persists_cancelled_status()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN) — perm:shp:manage 통과
        const string plantId = "P-SHP-CANCEL-A";
        const string orderId = "SHP-CANCEL-A-1";
        var requestedDate = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);

        // 1) 주문 생성(POST) — 신규 주문은 Draft. FK 부모 없음(PLANT_ID는 단순 컬럼).
        var createResp = await client.PostAsJsonAsync("/api/v1/shp/orders", new
        {
            orderId,
            customerName = "SHP Cancel Customer",
            plantId,
            requestedDate
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ORDER INSERT가 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<OrderDto>();
        created.Should().NotBeNull();
        created!.Status.Should().Be("Draft", "신규 주문은 Draft 상태로 생성돼야 한다");

        // 2) 취소(PUT /orders/{id}/cancel) → 204. SHP_DELIVERY_ORDER UPDATE(STATUS='Cancelled') 트리거.
        //    Draft에서 cancel은 도메인상 허용된다(가드는 Shipped/Cancelled만 차단).
        var cancelResp = await client.PutAsync($"/api/v1/shp/orders/{orderId}/cancel", content: null);
        var cancelBody = await cancelResp.Content.ReadAsStringAsync();
        cancelResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Draft→Cancelled 상태전이(UPDATE)가 성공해 204를 반환해야 한다. 응답 본문: {cancelBody}");

        // 3) 되읽기(GET /orders?plantId=)로 cancel이 실제로 영속·복원됐는지 검증.
        //    읽기경로(OrderRow.ToDomain→DeliveryOrder.Restore)가 DB STATUS 컬럼을 복원하므로 'Cancelled'여야 한다.
        //    'Draft'로 돌아오면 읽기경로가 STATUS를 무시(상태손실)하는 버그다(되읽기로 진단).
        var orders = await client.GetFromJsonAsync<List<OrderDto>>($"/api/v1/shp/orders?plantId={plantId}");
        orders.Should().NotBeNull();
        var reread = orders!.Single(o => o.Id == orderId);
        reread.Status.Should().Be("Cancelled",
            "cancel 후 되읽기 시 STATUS가 'Cancelled'로 영속·복원돼야 한다(읽기경로가 DB STATUS를 복원해야 함)");
    }

    // ── (B) Shipped 주문 cancel → 도메인 가드 400 ────────────────────────────────

    [Fact]
    public async Task CancelOrder_on_shipped_order_is_rejected_by_domain_guard()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN)
        const string plantId = "P-SHP-CANCEL-B";
        const string orderId = "SHP-CANCEL-B-1";
        var requestedDate = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);
        var shippedDate   = new DateTime(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc);

        // 1) 생성(Draft).
        var createResp = await client.PostAsJsonAsync("/api/v1/shp/orders", new
        {
            orderId,
            customerName = "SHP Cancel Guard Customer",
            plantId,
            requestedDate
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ORDER INSERT가 성공해야 한다. 응답 본문: {createBody}");

        // 2) 확정(Draft→Confirmed) → 204.
        var confirmResp = await client.PutAsync($"/api/v1/shp/orders/{orderId}/confirm", content: null);
        var confirmBody = await confirmResp.Content.ReadAsStringAsync();
        confirmResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Draft→Confirmed 전이가 성공해 204여야 한다. 응답 본문: {confirmBody}");

        // 3) 출하(Confirmed→Shipped) → 204. 이후 주문은 cancel 불가 상태가 된다.
        var shipResp = await client.PutAsJsonAsync($"/api/v1/shp/orders/{orderId}/ship", new { shippedDate });
        var shipBody = await shipResp.Content.ReadAsStringAsync();
        shipResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Confirmed→Shipped 전이가 성공해 204여야 한다. 응답 본문: {shipBody}");

        // 4) Shipped 주문 cancel 시도 → 409. DeliveryOrder.Cancel()이 Error.Conflict를 반환하고
        //    ToActionResult가 409 Conflict로 매핑한다(Wave C-α; 도메인 상태 가드가 PUT으로 노출되는지 실증).
        //    이 단계가 성공(204)으로 새면 출하 완료 주문이 취소되는 상태머신 무력화 버그다.
        var cancelResp = await client.PutAsync($"/api/v1/shp/orders/{orderId}/cancel", content: null);
        var cancelBody = await cancelResp.Content.ReadAsStringAsync();
        cancelResp.StatusCode.Should().Be(HttpStatusCode.Conflict,
            $"Shipped 주문 cancel은 도메인 가드 위반으로 409여야 한다(Cancel→Error.Conflict→Conflict). 응답 본문: {cancelBody}");

        // 5) 되읽기 — 가드로 거부됐으니 STATUS는 여전히 'Shipped'여야 한다(UPDATE가 일어나면 안 됨).
        var orders = await client.GetFromJsonAsync<List<OrderDto>>($"/api/v1/shp/orders?plantId={plantId}");
        orders.Should().NotBeNull();
        orders!.Single(o => o.Id == orderId).Status.Should().Be("Shipped",
            "cancel이 가드로 거부됐으므로 STATUS는 'Shipped'로 유지돼야 한다(실패 시 UPDATE 미수행)");
    }

    // DeliveryOrder 직렬화 형태(camelCase). Id=OrderId(PK), Status는 JsonStringEnumConverter로 문자열,
    // ShippedDate는 NULL 허용(Ship 전에는 null).
    private sealed record OrderDto(
        string Id, string CustomerName, string PlantId, DateTime RequestedDate, DateTime? ShippedDate, string Status);
}
