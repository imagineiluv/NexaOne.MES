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

    // ── Delivery Items (V029 SHP_DELIVERY_ITEM) ───────────────────────────────
    // GET /api/v1/shp/orders/{orderId}/items — V029 이전에는 SHP_DELIVERY_ITEM 테이블이 없어
    // SELECT가 'no such table'로 깨졌다. 이 read-smoke는 테이블 존재 + SQLite SELECT 방언을 증명한다.

    [Fact]
    public async Task GetItems_requires_auth_and_returns_ok()
    {
        const string orderId = "SHP-IT-ITEMS-RO";

        var anonymous = _factory.CreateClient();
        var anonResp = await anonymous.GetAsync($"/api/v1/shp/orders/{orderId}/items");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 요청은 [Authorize]에 의해 401이어야 한다");

        var client = _factory.CreateAuthenticatedClient();
        var okResp = await client.GetAsync($"/api/v1/shp/orders/{orderId}/items");
        var body = await okResp.Content.ReadAsStringAsync();
        okResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ITEM 목록 조회(SELECT)가 성공해야 한다. 응답 본문: {body}");

        var items = await okResp.Content.ReadFromJsonAsync<List<DeliveryItemDto>>();
        items.Should().NotBeNull("성공 응답은 JSON 배열(빈 배열 포함)이어야 한다");
    }

    [Fact]
    public async Task AddItem_persists_and_is_returned_by_get()
    {
        var client = _factory.CreateAuthenticatedClient();   // 기본 "*"(ADMIN) — perm:shp:manage 정책 통과
        const string orderId = "SHP-IT-ITEMS-W";
        const string itemId = "SHP-IT-ITEM-1";

        // 품목 추가(POST). 하니스는 FK OFF라 부모 주문 시드 없이 안전하다.
        var addResp = await client.PostAsJsonAsync($"/api/v1/shp/orders/{orderId}/items", new
        {
            itemId,
            productId = "PROD-SHP-1",
            plannedQty = 12.5m,
            lotId = "LOT-SHP-1"
        });
        var addBody = await addResp.Content.ReadAsStringAsync();
        addResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_DELIVERY_ITEM INSERT가 성공해야 한다. 응답 본문: {addBody}");

        var created = await addResp.Content.ReadFromJsonAsync<DeliveryItemDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(itemId, "생성된 품목의 식별자(Id)는 요청한 ItemId여야 한다");
        created.PlannedQty.Should().Be(12.5m, "INSERT한 PLANNED_QTY(DECIMAL)가 그대로 영속화돼야 한다");

        // 주문별 품목 조회로 영속화 확인(SHP_DELIVERY_ITEM SELECT).
        var items = await client.GetFromJsonAsync<List<DeliveryItemDto>>($"/api/v1/shp/orders/{orderId}/items");
        items.Should().NotBeNull();
        items!.Should().ContainSingle(i => i.Id == itemId)
            .Which.DeliveryOrderId.Should().Be(orderId, "INSERT한 DELIVERY_ORDER_ID가 그대로 영속화돼야 한다");
    }

    // ── Shipment History (V029 SHP_SHIPMENT_HISTORY) ──────────────────────────
    // GET /api/v1/shp/orders/{orderId}/shipment-history — V029 이전에는 SHP_SHIPMENT_HISTORY
    // 테이블이 없어 SELECT가 깨졌다. read-smoke로 테이블 존재 + SELECT 방언을 증명한다.

    [Fact]
    public async Task GetShipmentHistory_requires_auth_and_returns_ok()
    {
        const string orderId = "SHP-IT-HIST-RO";

        var anonymous = _factory.CreateClient();
        var anonResp = await anonymous.GetAsync($"/api/v1/shp/orders/{orderId}/shipment-history");
        anonResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "토큰 없는 요청은 [Authorize]에 의해 401이어야 한다");

        var client = _factory.CreateAuthenticatedClient();
        var okResp = await client.GetAsync($"/api/v1/shp/orders/{orderId}/shipment-history");
        var body = await okResp.Content.ReadAsStringAsync();
        okResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_SHIPMENT_HISTORY 목록 조회(SELECT)가 성공해야 한다. 응답 본문: {body}");

        var history = await okResp.Content.ReadFromJsonAsync<List<ShipmentHistoryDto>>();
        history.Should().NotBeNull("성공 응답은 JSON 배열(빈 배열 포함)이어야 한다");
    }

    [Fact]
    public async Task RecordShipment_persists_and_is_returned_by_get()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string orderId = "SHP-IT-HIST-W";
        const string historyId = "SHP-IT-HIST-1";

        // 출하 이력 기록(POST). 하니스는 FK OFF라 부모 주문 시드 없이 안전하다.
        var recResp = await client.PostAsJsonAsync($"/api/v1/shp/orders/{orderId}/shipment-history", new
        {
            historyId,
            shippedQty = 7.25m,
            shippedBy = "shp-it-user",
            carrier = "ACME-LOGISTICS",
            trackingNo = "TRK-SHP-1"
        });
        var recBody = await recResp.Content.ReadAsStringAsync();
        recResp.StatusCode.Should().Be(HttpStatusCode.OK,
            $"SHP_SHIPMENT_HISTORY INSERT가 성공해야 한다. 응답 본문: {recBody}");

        var created = await recResp.Content.ReadFromJsonAsync<ShipmentHistoryDto>();
        created.Should().NotBeNull();
        created!.Id.Should().Be(historyId, "생성된 이력의 식별자(Id)는 요청한 HistoryId여야 한다");
        created.ShippedQty.Should().Be(7.25m, "INSERT한 SHIPPED_QTY(DECIMAL)가 그대로 영속화돼야 한다");

        // 주문별 출하 이력 조회로 영속화 확인(SHP_SHIPMENT_HISTORY SELECT).
        var history = await client.GetFromJsonAsync<List<ShipmentHistoryDto>>(
            $"/api/v1/shp/orders/{orderId}/shipment-history");
        history.Should().NotBeNull();
        history!.Should().ContainSingle(h => h.Id == historyId)
            .Which.Carrier.Should().Be("ACME-LOGISTICS", "INSERT한 CARRIER가 그대로 영속화돼야 한다");
    }

    // DeliveryOrder 직렬화 형태(camelCase). Id는 OrderId(=PK), Status는 JsonStringEnumConverter로 문자열.
    private sealed record OrderDto(string Id, string CustomerName, string PlantId, DateTime RequestedDate, string Status);

    // DeliveryItem 직렬화 형태(camelCase). Id는 ItemId(=PK, AuditableEntity.Id).
    private sealed record DeliveryItemDto(
        string Id, string DeliveryOrderId, string ProductId, decimal PlannedQty, decimal? ActualQty, string? LotId);

    // ShipmentHistory 직렬화 형태(camelCase). Id는 HistoryId(=PK, AuditableEntity.Id).
    private sealed record ShipmentHistoryDto(
        string Id, string DeliveryOrderId, DateTime ShippedAt, decimal ShippedQty, string ShippedBy,
        string? Carrier, string? TrackingNo);
}
