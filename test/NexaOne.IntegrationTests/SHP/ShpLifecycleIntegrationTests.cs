using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SHP;

/// <summary>
/// SHP 출하 라이프사이클 상태전이 HTTP 통합 테스트 — 기존 SHPControllerIntegrationTests.cs(생성/읽기 스모크)를
/// 보완해, "쓰기 → 상태전이 → 되읽기" 경로가 SQLite에서 실제로 영속·복원되는지(테이블 존재 + 방언
/// SELECT/INSERT/UPDATE + 인증 + 직렬화 + 영속)를 end-to-end로 실증한다.
///
/// 검증 시나리오:
///   (A) CreateOrder(Draft) → Confirm → Ship(ShippedDate) → GET orders 되읽기로 STATUS/SHIPPED_DATE 영속 확인.
///       상태전이는 PUT으로 UPDATE를 트리거하며, 되읽기는 UPDATE 방언(SET ... WHERE)·영속을 실증한다.
///   (B) AddItem → SetActualQty(PUT) → GET items 되읽기로 ACTUAL_QTY(DECIMAL) 영속 확인.
///
/// 하니스 사실(TestApiFactory): 클래스별 고유 SQLite 임시 DB(GUID) + Foreign Keys=False(FK 비강제) +
/// db/migrations 부트스트랩. CreateAuthenticatedClient()는 기본 "*"(ADMIN)로 발급되어 perm:shp:manage 통과.
/// FK-OFF이므로 상태전이 대상 행(주문/품목)을 먼저 POST로 생성하면 교차참조 부모 시드 없이 성립한다.
/// </summary>
public sealed class ShpLifecycleIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public ShpLifecycleIntegrationTests(TestApiFactory factory) => _factory = factory;

    // ── (A) Order 상태전이: Create(Draft) → Confirm → Ship → 되읽기 ─────────────────

    [Fact]
    public async Task ConfirmOrder_requires_auth()
    {
        // 미인증 클라이언트는 상태전이 PUT에서도 401이어야 한다([Authorize] + perm:shp:manage).
        var anonymous = _factory.CreateClient();
        var resp = await anonymous.PutAsync("/api/v1/shp/orders/ANY-ORDER/confirm", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 상태전이 요청은 [Authorize]에 의해 401이어야 한다");
    }

    [Fact]
    public async Task CreateOrder_then_Confirm_then_Ship_persists_status_and_shippedDate()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — perm:shp:manage 통과
        const string plantId = "P-SHP-LIFECYCLE";
        const string orderId = "SHP-LC-ORDER-1";
        var requestedDate = new DateTime(2026, 6, 14, 0, 0, 0, DateTimeKind.Utc);
        var shippedDate   = new DateTime(2026, 6, 20, 9, 30, 0, DateTimeKind.Utc);

        // 1) 주문 생성(POST). 신규 주문은 Draft.
        var createResp = await client.PostAsJsonAsync("/api/v1/shp/orders", new
        {
            orderId,
            customerName = "SHP Lifecycle Customer",
            plantId,
            requestedDate
        });
        var createBody = await createResp.Content.ReadAsStringAsync();
        createResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ORDER INSERT가 성공해야 한다. 응답 본문: {createBody}");

        var created = await createResp.Content.ReadFromJsonAsync<OrderDto>();
        created.Should().NotBeNull();
        created!.Status.Should().Be("Draft", "신규 주문은 Draft 상태로 생성돼야 한다");

        // 2) 확정(PUT /orders/{id}/confirm) → 204. SHP_DELIVERY_ORDER UPDATE(STATUS='Confirmed') 트리거.
        //    이 단계가 성공해야 Ship(Confirmed에서만 가능)이 가능하다 — 도메인 상태 가드를 실증한다.
        var confirmResp = await client.PutAsync($"/api/v1/shp/orders/{orderId}/confirm", content: null);
        var confirmBody = await confirmResp.Content.ReadAsStringAsync();
        confirmResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Draft→Confirmed 상태전이(UPDATE)가 성공해 204를 반환해야 한다. 응답 본문: {confirmBody}");

        // 3) 출하(PUT /orders/{id}/ship, body=ShipOrderRequest{ShippedDate}) → 204.
        //    SHP_DELIVERY_ORDER UPDATE(STATUS='Shipped', SHIPPED_DATE=@ShippedDate) 트리거.
        var shipResp = await client.PutAsJsonAsync($"/api/v1/shp/orders/{orderId}/ship", new { shippedDate });
        var shipBody = await shipResp.Content.ReadAsStringAsync();
        shipResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"Confirmed→Shipped 상태전이(UPDATE)가 성공해 204를 반환해야 한다. 응답 본문: {shipBody}");

        // 4) 되읽기(GET /orders?plantId=...)로 상태전이가 실제로 영속·복원됐는지 검증한다.
        //    UPDATE는 DB에 STATUS='Shipped'/SHIPPED_DATE를 기록한다(쓰기 경로 정상). 따라서 되읽기에서도
        //    'Shipped'/ShippedDate가 복원돼야 한다. 만약 'Draft'/null로 돌아오면 읽기 경로가 DB의
        //    STATUS/SHIPPED_DATE 컬럼을 도메인으로 복원하지 못하는 상태손실 버그다(되읽기로 진단).
        var orders = await client.GetFromJsonAsync<List<OrderDto>>($"/api/v1/shp/orders?plantId={plantId}");
        orders.Should().NotBeNull();
        var reread = orders!.Single(o => o.Id == orderId);

        reread.Status.Should().Be("Shipped",
            "Confirm→Ship 후 되읽기 시 STATUS가 'Shipped'로 영속·복원돼야 한다. " +
            "'Draft'로 돌아오면 읽기 경로(OrderRow.ToDomain)가 DB STATUS 컬럼을 무시하고 " +
            "Create(=Draft 고정)만 호출하는 상태손실 버그다");
        reread.ShippedDate.Should().NotBeNull(
            "Ship 후 되읽기 시 SHIPPED_DATE(DB에 영속)가 복원돼야 한다. null이면 읽기 경로가 SHIPPED_DATE 컬럼을 버리는 버그다");
        reread.ShippedDate!.Value.Should().Be(shippedDate,
            "되읽은 SHIPPED_DATE는 Ship 시 전달한 ShippedDate와 일치해야 한다");
    }

    // ── (B) Item actual-qty 상태전이: AddItem → SetActualQty(PUT) → 되읽기 ──────────

    [Fact]
    public async Task SetActualQty_requires_auth()
    {
        var anonymous = _factory.CreateClient();
        var resp = await anonymous.PutAsJsonAsync("/api/v1/shp/items/ANY-ITEM/actual-qty", new { actualQty = 1.0m });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 actual-qty 갱신 요청은 [Authorize]에 의해 401이어야 한다");
    }

    [Fact]
    public async Task AddItem_then_SetActualQty_persists_actualQty()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — perm:shp:manage 통과
        const string orderId = "SHP-LC-ITEM-ORDER";
        const string itemId  = "SHP-LC-ITEM-1";

        // 1) 품목 추가(POST). FK-OFF라 부모 주문 시드 없이 성립. 신규 품목은 ACTUAL_QTY=NULL.
        var addResp = await client.PostAsJsonAsync($"/api/v1/shp/orders/{orderId}/items", new
        {
            itemId,
            productId = "PROD-SHP-LC",
            plannedQty = 100m,
            lotId = "LOT-SHP-LC"
        });
        var addBody = await addResp.Content.ReadAsStringAsync();
        addResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ITEM INSERT가 성공해야 한다. 응답 본문: {addBody}");

        var added = await addResp.Content.ReadFromJsonAsync<DeliveryItemDto>();
        added.Should().NotBeNull();
        added!.ActualQty.Should().BeNull("신규 품목의 ACTUAL_QTY는 아직 NULL이어야 한다");

        // 2) 실적 수량 설정(PUT /items/{id}/actual-qty, body=SetActualQtyRequest{ActualQty}) → 204.
        //    SHP_DELIVERY_ITEM UPDATE(ACTUAL_QTY=@ActualQty) 트리거.
        const decimal actualQty = 87.5m;
        var setResp = await client.PutAsJsonAsync($"/api/v1/shp/items/{itemId}/actual-qty", new { actualQty });
        var setBody = await setResp.Content.ReadAsStringAsync();
        setResp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            $"actual-qty 갱신(UPDATE)이 성공해 204를 반환해야 한다. 응답 본문: {setBody}");

        // 3) 되읽기(GET /orders/{id}/items)로 ACTUAL_QTY(DECIMAL) 영속·복원 검증.
        //    DeliveryItem 읽기 경로(Row.ToDomain)는 ACTUAL_QTY를 복원하므로 87.5가 되읽혀야 한다.
        var items = await client.GetFromJsonAsync<List<DeliveryItemDto>>($"/api/v1/shp/orders/{orderId}/items");
        items.Should().NotBeNull();
        var reread = items!.Single(i => i.Id == itemId);
        reread.ActualQty.Should().Be(actualQty,
            "SetActualQty(UPDATE) 후 되읽기 시 ACTUAL_QTY(DECIMAL)가 영속·복원돼야 한다");
        reread.PlannedQty.Should().Be(100m,
            "UPDATE는 ACTUAL_QTY만 바꾸며 PLANNED_QTY는 그대로 유지돼야 한다");
    }

    // DeliveryOrder 직렬화 형태(camelCase). Id=OrderId(PK), Status는 JsonStringEnumConverter로 문자열,
    // ShippedDate는 NULL 허용(Ship 전에는 null).
    private sealed record OrderDto(
        string Id, string CustomerName, string PlantId, DateTime RequestedDate, DateTime? ShippedDate, string Status);

    // DeliveryItem 직렬화 형태(camelCase). Id=ItemId(PK). ActualQty는 NULL 허용.
    private sealed record DeliveryItemDto(
        string Id, string DeliveryOrderId, string ProductId, decimal PlannedQty, decimal? ActualQty, string? LotId);
}
