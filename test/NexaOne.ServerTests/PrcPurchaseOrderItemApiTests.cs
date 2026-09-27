using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Dapper;
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
using NexaOne.ServiceContracts.Prc;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class PrcPurchaseOrderItemApiTests : IClassFixture<PrcPurchaseOrderItemApiTests.PrcFactory>
{
    private const string Secret = "prc-item-api-test-secret-key-at-least-32-bytes!!";
    private const string Issuer = "nexaone-prc-item-test";
    private readonly PrcFactory _factory;

    public PrcPurchaseOrderItemApiTests(PrcFactory factory) => _factory = factory;

    public sealed class PrcFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(),
            $"nexaone-prc-item-{Guid.NewGuid():N}.db");
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
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPurchaseOrderItemBridge>(sp =>
                {
                    var dataSource = new EesDataSource
                    {
                        Provider = sp.GetRequiredService<IDatabaseProvider>(),
                        ConnectionString = ConnString,
                    };
                    return new NexaOne.PRC.Module(dataSource,
                        new BusinessMasterDirectory(dataSource)).GetPurchaseOrderItemBridge();
                });
                services.AddSingleton<IPurchaseOrderHoldBridge>(sp =>
                {
                    var dataSource = new EesDataSource
                    {
                        Provider = sp.GetRequiredService<IDatabaseProvider>(),
                        ConnectionString = ConnString,
                    };
                    return new NexaOne.PRC.Module(dataSource,
                        new BusinessMasterDirectory(dataSource)).GetPurchaseOrderHoldBridge();
                });
                services.AddSingleton<IPurchaseOrderCommandBridge>(sp =>
                {
                    var dataSource = new EesDataSource
                    {
                        Provider = sp.GetRequiredService<IDatabaseProvider>(),
                        ConnectionString = ConnString,
                    };
                    return new NexaOne.PRC.Module(dataSource,
                        new BusinessMasterDirectory(dataSource)).GetPurchaseOrderCommandBridge();
                });
            });
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Draft_lines_update_header_total_and_use_jwt_actor()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = $"PO_{suffix}";
        var first = $"P_{suffix}_A";
        var second = $"P_{suffix}_B";
        Seed(id, first, second);
        var url = Url(id);

        (await _factory.CreateClient().PutAsJsonAsync($"{url}/{first}", new { quantity = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client("reader", Permissions.PrcRead)
            .PutAsJsonAsync($"{url}/{first}", new { quantity = 4 }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var manager = Client("buyer", Permissions.PrcManage);
        (await manager.PutAsJsonAsync($"{url}/{first}", new { quantity = 4, actorId = "spoofed" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.PutAsJsonAsync($"{url}/{second}", new { quantity = 6 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Scalar<decimal>("SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(10m);
        (await manager.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = id, plantId = "PLANT01", purchaseOrderName = "edited",
            vendorId = "V1", orderQuantity = 999m,
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        Scalar<decimal>("SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(10m, "header-form edits must not override the line aggregate");
        Scalar<string>("SELECT UPDATED_BY FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id AND PRODUCT_ID=@first",
            new { id, first }).Should().Be("buyer");

        var items = await Client("reader", Permissions.PrcRead)
            .GetFromJsonAsync<List<PurchaseOrderItem>>(url);
        items.Should().HaveCount(2);
        items!.Select(item => item.OrderQuantity).Should().BeEquivalentTo(new[] { 4m, 6m });

        (await manager.PutAsJsonAsync($"{url}/{first}", new { quantity = 2 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.DeleteAsync($"{url}/{second}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Scalar<decimal>("SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(2m);
        Scalar<long>("SELECT COUNT(*) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(1);
        (await manager.DeleteAsync($"{url}/{first}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Scalar<decimal>("SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(0m);
    }

    [Fact]
    public async Task Held_order_and_invalid_product_cannot_change_lines()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = $"PO_{suffix}";
        var product = $"P_{suffix}";
        Seed(id, product);
        var manager = Client("buyer", Permissions.PrcManage);
        var url = Url(id);

        (await manager.PutAsJsonAsync($"{url}/MISSING", new { quantity = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await manager.PutAsJsonAsync($"{url}/{product}", new { quantity = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await manager.PutAsJsonAsync($"{url}/{product}", new { quantity = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        Execute("UPDATE PRC_PURCHASE_ITEM SET INCOMING_QTY=1 WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await manager.PutAsJsonAsync($"{url}/{product}", new { quantity = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.DeleteAsync($"{url}/{product}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Execute("UPDATE PRC_PURCHASE_ITEM SET INCOMING_QTY=0 WHERE PURCHASE_ORDER_ID=@id", new { id });

        Execute("UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='Y' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await manager.PutAsJsonAsync($"{url}/{product}", new { quantity = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.DeleteAsync($"{url}/{product}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Execute("UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='N', STATUS='Ordered' WHERE PURCHASE_ORDER_ID=@id",
            new { id });
        (await manager.DeleteAsync($"{url}/{product}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Scalar<decimal>("SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(3m);
        (await manager.DeleteAsync($"{Url("NOT_FOUND")}/{product}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Hold_and_release_guard_order_and_line_writes_without_changing_status()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = $"PO_{suffix}";
        var product = $"P_{suffix}";
        Seed(id, product);
        var manager = Client("buyer", Permissions.PrcManage);
        var itemUrl = $"{Url(id)}/{product}";
        var holdUrl = $"/api/v1/prc/purchase-orders/{id}/hold";
        var releaseUrl = $"/api/v1/prc/purchase-orders/{id}/release";
        var orderUrl = $"/api/v1/prc/purchase-orders/{id}/order";
        var closeUrl = $"/api/v1/prc/purchase-orders/{id}/close";

        (await manager.PutAsJsonAsync(itemUrl, new { quantity = 3 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var held = await manager.PostAsync(holdUrl, null);
        held.StatusCode.Should().Be(HttpStatusCode.OK);
        (await held.Content.ReadFromJsonAsync<PurchaseOrderHoldState>())
            .Should().Be(new PurchaseOrderHoldState(id, true));
        Scalar<string>("SELECT IS_HOLD FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("Y");
        Scalar<string>("SELECT UPDATED_BY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("buyer");
        (await manager.PostAsync(holdUrl, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.PutAsJsonAsync(itemUrl, new { quantity = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.PostAsync(orderUrl, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        Scalar<string>("SELECT STATUS FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("Draft");

        var released = await manager.PostAsync(releaseUrl, null);
        released.StatusCode.Should().Be(HttpStatusCode.OK);
        (await released.Content.ReadFromJsonAsync<PurchaseOrderHoldState>())
            .Should().Be(new PurchaseOrderHoldState(id, false));
        (await manager.PostAsync(releaseUrl, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await manager.PutAsJsonAsync(itemUrl, new { quantity = 5 }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var ordered = await manager.PostAsync(orderUrl, null);
        ordered.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ordered.Content.ReadFromJsonAsync<PurchaseOrderCommandState>())
            .Should().Be(new PurchaseOrderCommandState(id, "Ordered"));
        Scalar<string>("SELECT STATUS FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("Ordered");

        (await manager.PostAsync(holdUrl, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await manager.PostAsync(closeUrl, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        Scalar<string>("SELECT STATUS FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("Ordered");
        (await manager.PostAsync(releaseUrl, null)).StatusCode.Should().Be(HttpStatusCode.OK);

        Execute("UPDATE PRC_PURCHASE_ORDER SET STATUS='Incoming' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await manager.PostAsync(holdUrl, null)).StatusCode.Should().Be(HttpStatusCode.Conflict);
        Execute("UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='Y' WHERE PURCHASE_ORDER_ID=@id", new { id });
        (await manager.PostAsync(releaseUrl, null)).StatusCode.Should().Be(HttpStatusCode.OK);
        Scalar<string>("SELECT STATUS FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("Incoming");
    }

    [Fact]
    public async Task Hold_endpoints_require_manage_permission_and_distinguish_missing_orders()
    {
        var id = $"PO_{Guid.NewGuid():N}";
        Seed(id);
        var holdUrl = $"/api/v1/prc/purchase-orders/{id}/hold";
        (await _factory.CreateClient().PostAsync(holdUrl, null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client("reader", Permissions.PrcRead).PostAsync(holdUrl, null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var manager = Client("buyer", Permissions.PrcManage);
        (await manager.PostAsync("/api/v1/prc/purchase-orders/NOT_FOUND/hold", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await manager.PostAsync($"/api/v1/prc/purchase-orders/{new string('x', 51)}/hold", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Scalar<string>("SELECT IS_HOLD FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("N");
    }

    [Fact]
    public async Task Order_and_close_require_manage_permission_and_validate_identity()
    {
        var id = $"PO_{Guid.NewGuid():N}";
        Seed(id);
        var orderUrl = $"/api/v1/prc/purchase-orders/{id}/order";
        (await _factory.CreateClient().PostAsync(orderUrl, null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client("reader", Permissions.PrcRead).PostAsync(orderUrl, null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var manager = Client("buyer", Permissions.PrcManage);
        (await manager.PostAsync("/api/v1/prc/purchase-orders/NOT_FOUND/order", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await manager.PostAsync($"/api/v1/prc/purchase-orders/{new string('x', 51)}/close", null))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Scalar<string>("SELECT STATUS FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be("Draft");
    }

    [Fact]
    public async Task Failed_header_total_update_rolls_back_the_new_line()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var id = $"PO_{suffix}";
        var product = $"P_{suffix}";
        Seed(id, product);
        var trigger = $"TR_PRC_FAIL_{suffix}";
        Execute($"""
            CREATE TRIGGER {trigger} BEFORE UPDATE OF ORDER_QTY ON PRC_PURCHASE_ORDER
            WHEN NEW.PURCHASE_ORDER_ID='{id}' AND NEW.ORDER_QTY>0
            BEGIN SELECT RAISE(ABORT, 'forced header failure'); END
            """, new { });
        var bridge = _factory.Services.GetRequiredService<IPurchaseOrderItemBridge>();
        try
        {
            var act = () => bridge.SaveDraftItemAsync(id, product, 4m, "buyer");
            await act.Should().ThrowAsync<SqliteException>();

            Scalar<long>("SELECT COUNT(*) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id", new { id })
                .Should().Be(0);
            Scalar<decimal>("SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
                .Should().Be(0m);
        }
        finally
        {
            Execute($"DROP TRIGGER IF EXISTS {trigger}", new { });
        }

        var recovered = await bridge.SaveDraftItemAsync(id, product, 4m, "buyer");
        recovered.IsSuccess.Should().BeTrue(recovered.Error.Description);
        recovered.Value.OrderQuantity.Should().Be(4m);
        Scalar<long>("SELECT COUNT(*) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(1);
        Scalar<decimal>("SELECT ORDER_QTY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(4m);
    }

    private HttpClient Client(string actor, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor) };
        claims.AddRange(permissions.Select(permission => new Claim(Permissions.ClaimType, permission)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private void Seed(string id, params string[] products)
    {
        _ = _factory.CreateClient();
        Execute("INSERT INTO PRC_PURCHASE_ORDER (PURCHASE_ORDER_ID,PLANT_ID,ORDER_QTY,STATUS,IS_HOLD) " +
                "VALUES (@id,'PLANT01',0,'Draft','N')", new { id });
        foreach (var product in products)
        {
            Execute("INSERT INTO MDM_PRODUCT (PRODUCT_ID,PRODUCT_NAME,PRODUCT_TYPE,UNIT,VALID_STATE) " +
                    "VALUES (@product,@product,'Material','EA','Valid')", new { product });
        }
    }

    private void Execute(string sql, object parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        connection.Execute(sql, parameters);
    }

    private T Scalar<T>(string sql, object parameters) where T : notnull
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        return connection.ExecuteScalar<T>(sql, parameters)
            ?? throw new InvalidOperationException("PRC test scalar returned no value.");
    }

    private static string Url(string id) => $"/api/v1/prc/purchase-orders/{id}/items";
}
