using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>게이트웨이 우선 SHP read E2E(S1) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩).
/// SHP_DELIVERY_ORDER/ITEM/HISTORY를 SqliteConnection 직접 INSERT로 시드한 뒤 명명 read 쿼리 3개
/// (DeliveryItemsByOrder/ShipmentHistoryByOrder/DeliveryOrderCountByStatus) 라운드트립을 검증한다. + 미인증 401.
/// 품목/이력 테이블의 주문 FK 컬럼은 DELIVERY_ORDER_ID(부모 SHP_DELIVERY_ORDER.ORDER_ID 참조).</summary>
public sealed class GatewayShpQueryTests : IClassFixture<GatewayShpQueryTests.ShpFactory>
{
    private const string Secret = "s1-shp-gateway-e2e-jwt-secret-key-at-least-32-bytes-long";
    private const string Issuer = "nexaone-shp-test";
    private readonly ShpFactory _factory;
    public GatewayShpQueryTests(ShpFactory factory) => _factory = factory;

    public sealed class ShpFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-shp-e2e-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    // 호스트를 한 번 띄워 SQLite 스키마(SHP_* 포함)를 보장한다(개발 SQLite 부트스트랩 경로).
    private void EnsureSchemaReady() => _ = _factory.CreateClient();

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "shp-e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    // SHP_DELIVERY_ORDER 1건 + SHP_DELIVERY_ITEM 2건 + SHP_SHIPMENT_HISTORY 1건 시드(실재 컬럼·NOT NULL 충족).
    // 감사 컬럼은 NOT NULL이므로 명시 INSERT한다. status는 enum.ToString() 패리티('Confirmed').
    private void SeedDelivery(string orderId)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO SHP_DELIVERY_ORDER
                (ORDER_ID, CUSTOMER_NAME, PLANT_ID, REQUESTED_DATE, STATUS,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@id, 'E2E 고객', 'P1', @now, 'Confirmed', 'TEST', @now, 'TEST', @now)";
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }

        InsertItem(conn, $"{orderId}_I1", orderId, "PROD_A", 100m, now);
        InsertItem(conn, $"{orderId}_I2", orderId, "PROD_B", 250m, now);

        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = @"INSERT INTO SHP_SHIPMENT_HISTORY
                (HISTORY_ID, DELIVERY_ORDER_ID, SHIPPED_AT, SHIPPED_QTY, SHIPPED_BY,
                 CARRIER, TRACKING_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@hid, @oid, @now, 350, 'shipper-1', 'DHL', 'TRK-001', 'TEST', @now, 'TEST', @now)";
            cmd.Parameters.AddWithValue("@hid", $"{orderId}_H1");
            cmd.Parameters.AddWithValue("@oid", orderId);
            cmd.Parameters.AddWithValue("@now", now);
            cmd.ExecuteNonQuery();
        }
    }

    private static void InsertItem(SqliteConnection conn, string itemId, string orderId, string productId, decimal plannedQty, string now)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SHP_DELIVERY_ITEM
            (ITEM_ID, DELIVERY_ORDER_ID, PRODUCT_ID, PLANNED_QTY,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@iid, @oid, @pid, @qty, 'TEST', @now, 'TEST', @now)";
        cmd.Parameters.AddWithValue("@iid", itemId);
        cmd.Parameters.AddWithValue("@oid", orderId);
        cmd.Parameters.AddWithValue("@pid", productId);
        cmd.Parameters.AddWithValue("@qty", plannedQty);
        cmd.Parameters.AddWithValue("@now", now);
        cmd.ExecuteNonQuery();
    }

    private static string OrderId() => "SHP_E2E_" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/SHP.DeliveryItemsByOrder",
            new Dictionary<string, object> { ["orderId"] = "ANY" });
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task DeliveryItemsByOrder_returns_seeded_items()
    {
        EnsureSchemaReady();
        var orderId = OrderId();
        SeedDelivery(orderId);
        var client = AuthedClient();

        var res = await client.PostAsJsonAsync("/api/v1/query/SHP.DeliveryItemsByOrder",
            new Dictionary<string, object> { ["orderId"] = orderId });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().HaveCount(2, "주문에 시드한 품목 2건이 라운드트립돼야 한다");
        rows.Should().Contain(r => r.ContainsKey("PRODUCT_ID") && r["PRODUCT_ID"].ToString() == "PROD_A");
        rows.Should().Contain(r => r.ContainsKey("PRODUCT_ID") && r["PRODUCT_ID"].ToString() == "PROD_B");
        rows.Should().OnlyContain(r => r.ContainsKey("PLANNED_QTY"), "품목 행은 PLANNED_QTY를 포함해야 한다");
    }

    [Fact]
    public async Task ShipmentHistoryByOrder_returns_seeded_history()
    {
        EnsureSchemaReady();
        var orderId = OrderId();
        SeedDelivery(orderId);
        var client = AuthedClient();

        var res = await client.PostAsJsonAsync("/api/v1/query/SHP.ShipmentHistoryByOrder",
            new Dictionary<string, object> { ["orderId"] = orderId });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().ContainSingle("주문에 시드한 출하 이력 1건이 라운드트립돼야 한다");
        rows![0].Should().ContainKey("SHIPPED_BY");
        rows![0]["SHIPPED_BY"].ToString().Should().Be("shipper-1");
    }

    [Fact]
    public async Task DeliveryOrderCountByStatus_returns_count_for_status()
    {
        EnsureSchemaReady();
        var orderId = OrderId();
        SeedDelivery(orderId); // STATUS='Confirmed' 1건

        var client = AuthedClient();
        var res = await client.PostAsJsonAsync("/api/v1/query/SHP.DeliveryOrderCountByStatus",
            new Dictionary<string, object> { ["status"] = "Confirmed", ["plantId"] = "P1" });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        var confirmed = rows!.SingleOrDefault(r => r.ContainsKey("STATUS") && r["STATUS"].ToString() == "Confirmed");
        confirmed.Should().NotBeNull("Confirmed 상태 그룹 행이 반환돼야 한다");
        confirmed!.Should().ContainKey("CNT");
        // 값은 Dictionary<string,object> 역직렬화로 JsonElement가 된다 — 다른 키 단언처럼 문자열 경유로 파싱한다.
        int.Parse(confirmed!["CNT"].ToString()!).Should().BeGreaterThanOrEqualTo(1, "시드한 Confirmed 주문이 카운트에 포함돼야 한다");
    }
}
