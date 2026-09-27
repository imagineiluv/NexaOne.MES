using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;
using NexaOne.Common.Security;
using NexaOne.Server.Gateway;
using NexaOne.ServiceContracts.Sls;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>실제 SLS 모듈 저장소를 거친 수주 초안·상태 전이와 JWT 경계를 검증합니다.</summary>
public sealed class SlsSalesOrderCommandApiTests
    : IClassFixture<SlsSalesRequestApiTests.SlsFactory>
{
    private const string Secret = "sls-request-api-test-secret-at-least-32-bytes!!";
    private const string Issuer = "nexaone-sls-request-test";
    private readonly SlsSalesRequestApiTests.SlsFactory _factory;

    public SlsSalesOrderCommandApiTests(SlsSalesRequestApiTests.SlsFactory factory) => _factory = factory;

    [Fact]
    public async Task Draft_write_requires_sls_manage_and_uses_jwt_actor()
    {
        var suffix = Suffix();
        var request = Draft(suffix);
        SeedReferences(suffix);

        (await _factory.CreateClient().PostAsJsonAsync(Url, request))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Client("reader", Permissions.SlsRead).PostAsJsonAsync(Url, request))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var response = await Client("seller", Permissions.SlsManage).PostAsJsonAsync(Url,
            new { request.SalesOrderId, request.SalesOrderName, request.PlantId,
                  request.CustomerId, request.ProductId, request.PlanStartDate,
                  request.PlanEndDate, request.PlanQty, actorId = "spoofed" });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<SalesOrderCommandState>())
            .Should().Be(new SalesOrderCommandState(request.SalesOrderId!, "Draft"));
        Scalar<string>("SELECT CREATED_BY FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be("seller");
        Scalar<string>("SELECT OWNER_ID FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be("seller");
    }

    [Fact]
    public async Task Held_and_wrong_source_orders_cannot_be_edited_deleted_or_advanced()
    {
        var suffix = Suffix();
        var request = Draft(suffix);
        SeedReferences(suffix);
        var client = Client("manager", Permissions.SlsManage);
        (await client.PostAsJsonAsync(Url, request)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(Url, request with { SalesOrderName = "edited" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Scalar<string>("SELECT SALES_ORDER_NAME FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be("edited");

        Execute("UPDATE SLS_SALES_ORDER SET IS_HOLD='Y' WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId });
        (await client.PostAsJsonAsync(Url, request with { SalesOrderName = "illegal" }))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"{Url}/{request.SalesOrderId}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"{Url}/{request.SalesOrderId}/confirm", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Scalar<string>("SELECT SALES_ORDER_NAME FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be("edited");

        Execute("UPDATE SLS_SALES_ORDER SET IS_HOLD='N' WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId });
        (await client.PostAsync($"{Url}/{request.SalesOrderId}/confirm", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"{Url}/{request.SalesOrderId}/confirm", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"{Url}/{request.SalesOrderId}/close", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Execute("UPDATE SLS_SALES_ORDER SET STATUS='Producing' WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId });
        (await client.PostAsync($"{Url}/{request.SalesOrderId}/close", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        Scalar<string>("SELECT STATUS FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be("Closed");
        (await client.DeleteAsync($"{Url}/{request.SalesOrderId}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Linked_request_blocks_deletion_but_unlinked_draft_can_be_deleted()
    {
        var suffix = Suffix();
        var request = Draft(suffix);
        SeedReferences(suffix);
        var client = Client("manager", Permissions.SlsManage);
        (await client.PostAsJsonAsync(Url, request)).StatusCode.Should().Be(HttpStatusCode.OK);

        var salesRequestId = $"SR_{suffix}";
        Execute("""
            INSERT INTO SLS_SALES_REQUEST (SALES_REQUEST_ID, SALES_ORDER_ID, STATUS)
            VALUES (@salesRequestId, @salesOrderId, 'Confirmed')
            """, new { salesRequestId, salesOrderId = request.SalesOrderId });
        (await client.DeleteAsync($"{Url}/{request.SalesOrderId}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        Scalar<int>("SELECT COUNT(*) FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be(1);

        Execute("DELETE FROM SLS_SALES_REQUEST WHERE SALES_REQUEST_ID=@salesRequestId",
            new { salesRequestId });
        (await client.DeleteAsync($"{Url}/{request.SalesOrderId}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        Scalar<int>("SELECT COUNT(*) FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be(0);
    }

    [Fact]
    public async Task Invalid_quantity_or_inactive_master_cannot_create_draft()
    {
        var suffix = Suffix();
        var request = Draft(suffix);
        SeedReferences(suffix);
        var client = Client("manager", Permissions.SlsManage);
        (await client.PostAsJsonAsync(Url, request with { PlanQty = 0 }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Execute("UPDATE MDM_CUSTOMER SET IS_ACTIVE=0 WHERE CUSTOMER_ID=@id",
            new { id = request.CustomerId });
        (await client.PostAsJsonAsync(Url, request))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        Scalar<int>("SELECT COUNT(*) FROM SLS_SALES_ORDER WHERE SALES_ORDER_ID=@id",
            new { id = request.SalesOrderId }).Should().Be(0);
    }

    private const string Url = "/api/v1/sls/sales-orders";

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private static SalesOrderCommandController.SaveDraftRequest Draft(string suffix) => new(
        $"SO_{suffix}", "draft", $"P_{suffix}", $"C_{suffix}", $"I_{suffix}",
        new DateTime(2040, 9, 1), new DateTime(2040, 9, 30), 12.5m);

    private HttpClient Client(string actor, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actor) };
        claims.AddRange(permissions.Select(permission =>
            new Claim(Permissions.ClaimType, permission)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims,
            expires: DateTime.UtcNow.AddMinutes(10),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private void SeedReferences(string suffix)
    {
        _ = _factory.CreateClient();
        Execute("""
            INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES (@plant, @plant);
            INSERT INTO MDM_CUSTOMER (CUSTOMER_ID, CUSTOMER_NAME, IS_ACTIVE)
                VALUES (@customer, @customer, 1);
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE)
                VALUES (@product, @product, 'FinishedGoods', 'EA', 'Valid');
            """, new { plant = $"P_{suffix}", customer = $"C_{suffix}", product = $"I_{suffix}" });
    }

    private void Execute(string sql, object parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        connection.Execute(sql, parameters);
    }

    private T Scalar<T>(string sql, object parameters)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        return connection.ExecuteScalar<T>(sql, parameters)!;
    }
}
