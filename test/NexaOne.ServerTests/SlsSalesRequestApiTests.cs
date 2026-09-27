using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Common.Security;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.SHP.Infrastructure;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Sls;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>실제 SLS 모듈 bridge를 SQLite API에 연결해 요청→수주 원자성·권한을 검증합니다.</summary>
public sealed class SlsSalesRequestApiTests : IClassFixture<SlsSalesRequestApiTests.SlsFactory>
{
    private const string Secret = "sls-request-api-test-secret-at-least-32-bytes!!";
    private const string Issuer = "nexaone-sls-request-test";
    private readonly SlsFactory _factory;

    public SlsSalesRequestApiTests(SlsFactory factory) => _factory = factory;

    public sealed class SlsFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-sls-request-{Guid.NewGuid():N}.db");
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
            // 실제 SLS 조립·저장소를 사용하되 정적 ApplicationServer의 in-proc 재시작은 피한다.
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton(sp =>
                {
                    var dataSource = new EesDataSource
                    {
                        Provider = sp.GetRequiredService<IDatabaseProvider>(),
                        ConnectionString = ConnString,
                    };
                    return new NexaOne.SLS.Module(dataSource, new BusinessMasterDirectory(dataSource),
                        new SalesOrderShipmentIntake(), new SalesOrderShipmentEvidence());
                });
                services.AddSingleton<ISalesRequestBridge>(sp =>
                    sp.GetRequiredService<NexaOne.SLS.Module>().GetSalesRequestBridge());
                services.AddSingleton<ISalesOrderDeliveryBridge>(sp =>
                    sp.GetRequiredService<NexaOne.SLS.Module>().GetSalesOrderDeliveryBridge());
                services.AddSingleton<ISalesOrderCommandBridge>(sp =>
                    sp.GetRequiredService<NexaOne.SLS.Module>().GetSalesOrderCommandBridge());
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* temp cleanup best effort */ }
        }
    }

    [Fact]
    public async Task Request_writes_require_authentication_and_sls_manage()
    {
        var suffix = Suffix();
        var body = Draft($"SR_{suffix}", $"C_{suffix}", $"I_{suffix}");
        (await _factory.CreateClient().PostAsJsonAsync("/api/v1/sls/sales-requests", body))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client("reader", Permissions.SlsRead).PostAsJsonAsync("/api/v1/sls/sales-requests", body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Client("reader", Permissions.SlsRead).PostAsJsonAsync(
            $"/api/v1/sls/sales-requests/{body.SalesRequestId}/receipt", Receipt("SO_FORBIDDEN", "PLANT01")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _factory.CreateClient().PostAsync(
            $"/api/v1/sls/sales-requests/{body.SalesRequestId}/withdraw", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client("reader", Permissions.SlsRead).PostAsync(
            $"/api/v1/sls/sales-requests/{body.SalesRequestId}/withdraw", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Count("SLS_SALES_REQUEST", "SALES_REQUEST_ID", body.SalesRequestId!).Should().Be(0);
    }

    [Fact]
    public async Task Draft_withdrawal_preserves_audit_and_blocks_receipt_or_replay()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var orderId = $"SO_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        SeedReferences(plant, customer, product);
        (await Client("creator", Permissions.SlsManage).PostAsJsonAsync(
            "/api/v1/sls/sales-requests", Draft(requestId, customer, product)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var manager = Client("withdrawer", Permissions.SlsManage);
        var withdraw = await manager.PostAsync($"/api/v1/sls/sales-requests/{requestId}/withdraw", null);
        withdraw.StatusCode.Should().Be(HttpStatusCode.OK);
        (await withdraw.Content.ReadFromJsonAsync<SalesRequestState>())
            .Should().Be(new SalesRequestState(requestId, "Cancelled", null));
        ReadRequest(requestId).Should().Be(("Cancelled", null, "creator", "withdrawer"));

        (await manager.PostAsync($"/api/v1/sls/sales-requests/{requestId}/withdraw", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt(orderId, plant))).StatusCode.Should().Be(HttpStatusCode.Conflict);
        Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(0);
        ReadRequest(requestId).Should().Be(("Cancelled", null, "creator", "withdrawer"));
    }

    [Fact]
    public async Task Missing_or_confirmed_request_cannot_be_withdrawn()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var orderId = $"SO_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        SeedReferences(plant, customer, product);
        var manager = Client("sales-manager", Permissions.SlsManage);
        (await manager.PostAsync($"/api/v1/sls/sales-requests/{requestId}/withdraw", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await manager.PostAsJsonAsync("/api/v1/sls/sales-requests", Draft(requestId, customer, product)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt(orderId, plant))).StatusCode.Should().Be(HttpStatusCode.OK);

        (await manager.PostAsync($"/api/v1/sls/sales-requests/{requestId}/withdraw", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        ReadRequest(requestId).Should().Be(("Confirmed", orderId, "sales-manager", "sales-manager"));
        Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(1);
    }

    [Fact]
    public async Task Receipt_creates_a_draft_order_and_confirms_only_its_request()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var orderId = $"SO_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        var actor = $"sales-{suffix}";
        SeedReferences(plant, customer, product);
        var client = Client(actor, Permissions.SlsManage);

        var create = await client.PostAsJsonAsync("/api/v1/sls/sales-requests",
            new { salesRequestId = requestId, salesRequestName = "9월 요청", customerId = customer,
                productId = product, requestDate = "2040-09-01", requestQty = 12.5m,
                actorId = "spoofed-user", status = "Confirmed" });
        create.StatusCode.Should().Be(HttpStatusCode.OK);
        (await create.Content.ReadFromJsonAsync<SalesRequestState>())
            .Should().Be(new SalesRequestState(requestId, "Draft", null));
        ReadRequest(requestId).Should().Be(("Draft", null, actor, actor));

        var receive = await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt(orderId, plant));
        receive.StatusCode.Should().Be(HttpStatusCode.OK);
        (await receive.Content.ReadFromJsonAsync<SalesRequestState>())
            .Should().Be(new SalesRequestState(requestId, "Confirmed", orderId));
        ReadRequest(requestId).Should().Be(("Confirmed", orderId, actor, actor));
        ReadOrder(orderId).Should().Be(("Draft", plant, customer, product, 12.5m, actor, actor));

        var replay = await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt(orderId, plant));
        replay.StatusCode.Should().Be(HttpStatusCode.Conflict);
        Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(1);
        ReadRequest(requestId).Should().Be(("Confirmed", orderId, actor, actor));
    }

    [Fact]
    public async Task Sales_order_insert_failure_rolls_back_receipt_and_same_command_can_retry()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var orderId = $"SO_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        const string actor = "receipt-rollback";
        SeedReferences(plant, customer, product);
        var bridge = _factory.Services.GetRequiredService<ISalesRequestBridge>();
        var created = await bridge.CreateDraftAsync(new SalesRequestDraftCommand(
            requestId, "판매 요청", customer, product, new DateTime(2040, 9, 1), 12.5m, actor));
        created.IsSuccess.Should().BeTrue(created.Error.Description);

        var receipt = new SalesRequestReceiptCommand(requestId, orderId, plant, "9월 수주",
            new DateTime(2040, 9, 1), new DateTime(2040, 9, 30), actor);
        var trigger = $"sls_receipt_failure_{suffix}";
        Execute($"""
            CREATE TRIGGER {trigger} BEFORE INSERT ON SLS_SALES_ORDER
            BEGIN SELECT RAISE(ABORT, 'injected sales order insert failure'); END
            """, _ => { });
        try
        {
            await Assert.ThrowsAsync<SqliteException>(() => bridge.ReceiveAsync(receipt));
            ReadRequest(requestId).Should().Be(("Draft", null, actor, actor));
            Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(0);
        }
        finally
        {
            Execute($"DROP TRIGGER {trigger}", _ => { });
        }

        var recovered = await bridge.ReceiveAsync(receipt);
        recovered.IsSuccess.Should().BeTrue(recovered.Error.Description);
        recovered.Value.Should().Be(new SalesRequestState(requestId, "Confirmed", orderId));
        Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(1);
        var replay = await bridge.ReceiveAsync(receipt);
        replay.IsFailure.Should().BeTrue();
        replay.Error.Code.Should().Be("SLS_REQUEST_NOT_RECEIVABLE");
        Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(1);
    }

    [Fact]
    public async Task Duplicate_order_id_rolls_back_request_transition()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var orderId = $"SO_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        SeedReferences(plant, customer, product);
        var client = Client("sales-manager", Permissions.SlsManage);
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests", Draft(requestId, customer, product)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Execute("INSERT INTO SLS_SALES_ORDER (SALES_ORDER_ID, PLANT_ID, PLAN_QTY, STATUS) " +
                "VALUES (@id, @plant, 1, 'Closed')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", orderId);
            cmd.Parameters.AddWithValue("@plant", plant);
        });

        var receive = await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt(orderId, plant));
        receive.StatusCode.Should().Be(HttpStatusCode.Conflict);
        ReadRequest(requestId).Should().Be(("Draft", null, "sales-manager", "sales-manager"));
        ReadOrder(orderId).Status.Should().Be("Closed", "기존 수주를 덮어쓰면 안 된다");
        Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(1);
    }

    [Fact]
    public async Task Invalid_quantity_or_reference_never_creates_a_request()
    {
        var suffix = Suffix();
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        SeedReferences(plant, customer, product);
        var client = Client("sales-manager", Permissions.SlsManage);
        var zeroId = $"ZERO_{suffix}";
        var invalidId = $"BAD_{suffix}";
        var precisionId = $"PREC_{suffix}";
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests",
            Draft(zeroId, customer, product) with { RequestQty = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests",
            Draft(invalidId, customer, "UNKNOWN_ITEM")))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests",
            Draft(precisionId, customer, product) with { RequestQty = 1.00001m }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Count("SLS_SALES_REQUEST", "SALES_REQUEST_ID", zeroId).Should().Be(0);
        Count("SLS_SALES_REQUEST", "SALES_REQUEST_ID", invalidId).Should().Be(0);
        Count("SLS_SALES_REQUEST", "SALES_REQUEST_ID", precisionId).Should().Be(0);
    }

    [Fact]
    public async Task Inactive_reference_and_bad_plan_cannot_partially_receive()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var orderId = $"SO_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        SeedReferences(plant, customer, product);
        var client = Client("sales-manager", Permissions.SlsManage);
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests", Draft(requestId, customer, product)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var invalidPlan = Receipt(orderId, plant) with { PlanEndDate = new DateTime(2040, 8, 31) };
        (await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt", invalidPlan))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Execute("UPDATE MDM_CUSTOMER SET IS_ACTIVE = 0 WHERE CUSTOMER_ID = @id",
            cmd => cmd.Parameters.AddWithValue("@id", customer));
        (await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt(orderId, plant))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Execute("UPDATE MDM_CUSTOMER SET IS_ACTIVE = 1 WHERE CUSTOMER_ID = @id",
            cmd => cmd.Parameters.AddWithValue("@id", customer));
        (await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt(orderId, "UNKNOWN_PLANT"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        ReadRequest(requestId).Status.Should().Be("Draft");
        Count("SLS_SALES_ORDER", "SALES_ORDER_ID", orderId).Should().Be(0);
    }

    [Fact]
    public async Task Missing_request_and_duplicate_request_id_are_distinct_failures()
    {
        var suffix = Suffix();
        var requestId = $"SR_{suffix}";
        var plant = $"P_{suffix}";
        var customer = $"C_{suffix}";
        var product = $"I_{suffix}";
        SeedReferences(plant, customer, product);
        var client = Client("sales-manager", Permissions.SlsManage);
        (await client.PostAsJsonAsync($"/api/v1/sls/sales-requests/{requestId}/receipt",
            Receipt($"SO_{suffix}", plant))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests", Draft(requestId, customer, product)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/api/v1/sls/sales-requests", Draft(requestId, customer, product)))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Count("SLS_SALES_REQUEST", "SALES_REQUEST_ID", requestId).Should().Be(1);
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static SalesRequestController.CreateRequest Draft(string requestId, string customerId, string productId)
        => new(requestId, "판매 요청", customerId, productId, new DateTime(2040, 9, 1), 12.5m);

    private static SalesRequestController.ReceiveRequest Receipt(string orderId, string plantId)
        => new(orderId, plantId, "9월 수주", new DateTime(2040, 9, 1), new DateTime(2040, 9, 30));

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
        // table/key are only fixed test literals supplied above, never user data.
        command.CommandText = $"SELECT COUNT(1) FROM {table} WHERE {key} = @id";
        command.Parameters.AddWithValue("@id", id);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private (string Status, string? SalesOrderId, string CreatedBy, string UpdatedBy) ReadRequest(string id)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT STATUS, SALES_ORDER_ID, CREATED_BY, UPDATED_BY " +
                              "FROM SLS_SALES_REQUEST WHERE SALES_REQUEST_ID = @id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return (reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.GetString(2), reader.GetString(3));
    }

    private (string Status, string Plant, string Customer, string Product, decimal Qty,
        string Owner, string CreatedBy) ReadOrder(string id)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT STATUS, PLANT_ID, CUSTOMER_ID, PRODUCT_ID, PLAN_QTY, OWNER_ID, CREATED_BY " +
                              "FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID = @id";
        command.Parameters.AddWithValue("@id", id);
        using var reader = command.ExecuteReader();
        reader.Read().Should().BeTrue();
        return (reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
            Convert.ToDecimal(reader.GetValue(4)),
            reader.IsDBNull(5) ? string.Empty : reader.GetString(5), reader.GetString(6));
    }
}
