using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Common.Security;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Sls;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>실제 SLS·SHP 저장소를 한 SQLite API 호스트에 연결해 출하 인계 원자성을 검증합니다.</summary>
public sealed class SlsSalesOrderDeliveryApiTests
    : IClassFixture<SlsSalesRequestApiTests.SlsFactory>
{
    private const string Secret = "sls-request-api-test-secret-at-least-32-bytes!!";
    private const string Issuer = "nexaone-sls-request-test";
    private readonly SlsSalesRequestApiTests.SlsFactory _factory;

    public SlsSalesOrderDeliveryApiTests(SlsSalesRequestApiTests.SlsFactory factory) => _factory = factory;

    [Fact]
    public async Task Delivery_request_requires_authentication_and_sls_manage()
    {
        var id = $"SO_{Suffix()}";
        var url = Url(id);
        var body = Request($"DO_{Suffix()}", $"DI_{Suffix()}");
        (await _factory.CreateClient().PostAsJsonAsync(url, body))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client("reader", Permissions.SlsRead).PostAsJsonAsync(url, body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client("manager", Permissions.SlsManage).PostAsJsonAsync(url, body))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Received_sales_order_creates_one_draft_shipment_with_jwt_audit()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var salesOrderId = $"SO_{suffix}";
        var deliveryOrderId = $"DO_{suffix}";
        var deliveryItemId = $"DI_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        var actor = $"seller-{suffix}";
        SeedReferences(plant, customer, product);
        var client = Client(actor, Permissions.SlsManage);
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests", new SalesRequestController.CreateRequest(
            requestId, "판매 요청", customer, product, new DateTime(2040, 9, 1), 12.5m)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            new SalesRequestController.ReceiveRequest(salesOrderId, plant, "9월 수주",
                new DateTime(2040, 9, 1), new DateTime(2040, 9, 30))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await client.PostAsJsonAsync(Url(salesOrderId), new
        {
            deliveryOrderId,
            deliveryItemId,
            actorId = "spoofed-user",
            status = "Shipped",
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SalesOrderDeliveryState>())
            .Should().Be(new SalesOrderDeliveryState(salesOrderId, deliveryOrderId, "Confirmed"));
        ReadSalesOrder(salesOrderId).Should().Be(("Confirmed", deliveryOrderId, actor));
        ReadDeliveryOrder(deliveryOrderId).Should().Be(("Draft", customer, plant, actor, actor));
        ReadDeliveryItem(deliveryItemId).Should().Be((deliveryOrderId, product, 12.5m, actor));
        var list = await Client(actor, Permissions.SlsRead).PostAsJsonAsync(
            "/api/v1/query/SLS.SalesOrderList", new { plantId = plant });
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var orders = await list.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>();
        orders.Should().ContainSingle(row => row["SALES_ORDER_ID"].GetString() == salesOrderId)
            .Which["DELIVERY_ORDER_ID"].GetString().Should().Be(deliveryOrderId);

        (await client.PostAsJsonAsync(Url(salesOrderId), Request(deliveryOrderId, deliveryItemId)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Count("SHP_DELIVERY_ORDER", "ORDER_ID", deliveryOrderId).Should().Be(1);
        Count("SHP_DELIVERY_ITEM", "ITEM_ID", deliveryItemId).Should().Be(1);
    }

    [Fact]
    public async Task Confirmed_unheld_order_can_request_delivery_once()
    {
        var suffix = Suffix();
        var salesOrderId = $"SO_{suffix}";
        var deliveryOrderId = $"DO_{suffix}";
        SeedValidOrder(suffix, "Confirmed");
        var response = await Client("manager", Permissions.SlsManage).PostAsJsonAsync(
            Url(salesOrderId), Request(deliveryOrderId, $"DI_{suffix}"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ReadSalesOrder(salesOrderId).Should().Be(("Confirmed", deliveryOrderId, "manager"));
    }

    [Theory]
    [InlineData("Draft", "Y", 0)]
    [InlineData("Closed", "N", 0)]
    [InlineData("Draft", "N", 1)]
    public async Task Held_closed_or_partially_delivered_order_cannot_request_shipment(
        string status, string hold, int deliveredQty)
    {
        var suffix = Suffix();
        var salesOrderId = $"SO_{suffix}";
        var deliveryOrderId = $"DO_{suffix}";
        SeedValidOrder(suffix, status, hold, deliveredQty);
        var response = await Client("manager", Permissions.SlsManage).PostAsJsonAsync(
            Url(salesOrderId), Request(deliveryOrderId, $"DI_{suffix}"));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ReadSalesOrder(salesOrderId).Should().Be((status, null, "SYSTEM"));
        Count("SHP_DELIVERY_ORDER", "ORDER_ID", deliveryOrderId).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Duplicate_shipment_order_or_item_id_rolls_back_sales_link(bool itemConflict)
    {
        var suffix = Suffix();
        var salesOrderId = $"SO_{suffix}";
        var deliveryOrderId = $"DO_{suffix}";
        var deliveryItemId = $"DI_{suffix}";
        SeedValidOrder(suffix, "Draft");
        if (itemConflict)
        {
            var priorOrder = $"OLD_{suffix}";
            SeedShipmentOrder(priorOrder);
            Execute("INSERT INTO SHP_DELIVERY_ITEM (ITEM_ID, DELIVERY_ORDER_ID, PRODUCT_ID, PLANNED_QTY) " +
                    "VALUES (@item, @orderId, 'existing-product', 1)", cmd =>
            {
                cmd.Parameters.AddWithValue("@item", deliveryItemId);
                cmd.Parameters.AddWithValue("@orderId", priorOrder);
            });
        }
        else
        {
            SeedShipmentOrder(deliveryOrderId);
        }

        var response = await Client("manager", Permissions.SlsManage).PostAsJsonAsync(
            Url(salesOrderId), Request(deliveryOrderId, deliveryItemId));
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ReadSalesOrder(salesOrderId).Should().Be(("Draft", null, "SYSTEM"));
        Count("SHP_DELIVERY_ORDER", "ORDER_ID", deliveryOrderId).Should().Be(itemConflict ? 0 : 1);
        Count("SHP_DELIVERY_ITEM", "ITEM_ID", deliveryItemId).Should().Be(itemConflict ? 1 : 0);
    }

    [Fact]
    public async Task Inactive_customer_or_invalid_plan_cannot_create_shipment()
    {
        var suffix = Suffix();
        var salesOrderId = $"SO_{suffix}";
        var deliveryOrderId = $"DO_{suffix}";
        SeedValidOrder(suffix, "Draft");
        var client = Client("manager", Permissions.SlsManage);
        Execute("UPDATE MDM_CUSTOMER SET IS_ACTIVE = 0 WHERE CUSTOMER_ID = @id",
            cmd => cmd.Parameters.AddWithValue("@id", $"C_{suffix}"));
        (await client.PostAsJsonAsync(Url(salesOrderId), Request(deliveryOrderId, $"DI_{suffix}")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Execute("UPDATE MDM_CUSTOMER SET IS_ACTIVE = 1 WHERE CUSTOMER_ID = @id",
            cmd => cmd.Parameters.AddWithValue("@id", $"C_{suffix}"));
        Execute("UPDATE SLS_SALES_ORDER SET PLAN_END_DATE = NULL WHERE SALES_ORDER_ID = @id",
            cmd => cmd.Parameters.AddWithValue("@id", salesOrderId));
        (await client.PostAsJsonAsync(Url(salesOrderId), Request(deliveryOrderId, $"DI_{suffix}")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ReadSalesOrder(salesOrderId).Should().Be(("Draft", null, "SYSTEM"));
        Count("SHP_DELIVERY_ORDER", "ORDER_ID", deliveryOrderId).Should().Be(0);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
    private static string Url(string salesOrderId)
        => $"/api/v1/sls/sales-orders/{salesOrderId}/delivery-request";
    private static SalesOrderDeliveryController.DeliveryRequest Request(string orderId, string itemId)
        => new(orderId, itemId);

    private HttpClient Client(string actor, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor) };
        claims.AddRange(permissions.Select(p => new Claim(Permissions.ClaimType, p)));
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Issuer, claims,
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private void SeedValidOrder(string suffix, string status, string hold = "N", int deliveredQty = 0)
    {
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        SeedReferences(plant, customer, product);
        Execute("""
            INSERT INTO SLS_SALES_ORDER
                (SALES_ORDER_ID, PLANT_ID, CUSTOMER_ID, PRODUCT_ID, PLAN_END_DATE,
                 PLAN_QTY, DELIVERED_QTY, STATUS, IS_HOLD)
            VALUES (@id, @plant, @customer, @product, @due, 12.5, @deliveredQty, @status, @hold)
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@id", $"SO_{suffix}");
            cmd.Parameters.AddWithValue("@plant", plant);
            cmd.Parameters.AddWithValue("@customer", customer);
            cmd.Parameters.AddWithValue("@product", product);
            cmd.Parameters.AddWithValue("@due", new DateTime(2040, 9, 30));
            cmd.Parameters.AddWithValue("@deliveredQty", deliveredQty);
            cmd.Parameters.AddWithValue("@status", status);
            cmd.Parameters.AddWithValue("@hold", hold);
        });
    }

    private void SeedReferences(string plant, string customer, string product)
    {
        _ = _factory.CreateClient(); // schema ready
        Execute("INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES (@id, @id)",
            cmd => cmd.Parameters.AddWithValue("@id", plant));
        Execute("INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE) VALUES (@id, @id, 1)",
            cmd => cmd.Parameters.AddWithValue("@id", customer));
        Execute("INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE) " +
                "VALUES (@id, @id, 'FinishedGoods', 'EA', 'Valid')",
            cmd => cmd.Parameters.AddWithValue("@id", product));
    }

    private void SeedShipmentOrder(string deliveryOrderId)
        => Execute("""
            INSERT INTO SHP_DELIVERY_ORDER
                (ORDER_ID, CUSTOMER_NAME, PLANT_ID, REQUESTED_DATE, STATUS, CREATED_BY, UPDATED_BY)
            VALUES (@id, 'existing-customer', 'existing-plant', @due, 'Draft', 'SYSTEM', 'SYSTEM')
            """, cmd =>
        {
            cmd.Parameters.AddWithValue("@id", deliveryOrderId);
            cmd.Parameters.AddWithValue("@due", new DateTime(2040, 9, 30));
        });

    private void Execute(string sql, Action<SqliteCommand> bind)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        command.ExecuteNonQuery();
    }

    private int Count(string table, string key, string id)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        // Table and key are fixed test literals above, never request data.
        command.CommandText = $"SELECT COUNT(1) FROM {table} WHERE {key} = @id";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private (string Status, string? DeliveryOrderId, string UpdatedBy) ReadSalesOrder(string id)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT STATUS, DELIVERY_ORDER_ID, UPDATED_BY FROM SLS_SALES_ORDER " +
                              "WHERE SALES_ORDER_ID = @id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2));
    }

    private (string Status, string Customer, string Plant, string CreatedBy, string UpdatedBy)
        ReadDeliveryOrder(string id)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT STATUS, CUSTOMER_NAME, PLANT_ID, CREATED_BY, UPDATED_BY " +
                              "FROM SHP_DELIVERY_ORDER WHERE ORDER_ID = @id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return (reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetString(3), reader.GetString(4));
    }

    private (string OrderId, string Product, decimal Qty, string CreatedBy) ReadDeliveryItem(string id)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DELIVERY_ORDER_ID, PRODUCT_ID, PLANNED_QTY, CREATED_BY " +
                              "FROM SHP_DELIVERY_ITEM WHERE ITEM_ID = @id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return (reader.GetString(0), reader.GetString(1), Convert.ToDecimal(reader.GetValue(2)),
            reader.GetString(3));
    }
}
