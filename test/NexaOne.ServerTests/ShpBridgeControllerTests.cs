using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.Common;
using NexaOne.ServiceContracts.Shp;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>SHP 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008) — modules OFF + 가짜 IShipmentBridge 주입으로
/// 읽기 200·쓰기 권한(403/200)·상태전이(204/Conflict→409)를 Spring/ALC 없이 결정적으로 검증한다.</summary>
public sealed class ShpBridgeControllerTests : IClassFixture<ShpBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-shp-bridge-e2e-jwt-secret-key-at-least-32b!!";
    private const string Issuer = "nexaone-shp-bridge-test";
    private readonly BridgeFactory _factory;
    public ShpBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-shp-bridge-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            builder.ConfigureTestServices(s => s.AddSingleton<IShipmentBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private sealed class FakeBridge : IShipmentBridge
    {
        public Task<IReadOnlyList<DeliveryOrderDto>> GetOrdersByPlantAsync(
            string plantId, DateTime? from, DateTime? to, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DeliveryOrderDto>>(
                new[] { new DeliveryOrderDto("DO1", "ACME", plantId, DateTime.UtcNow, null, "Draft", 10m) });

        public Task<Result<DeliveryOrderDto>> CreateOrderAsync(
            string orderId, string customerName, string plantId, DateTime requestedDate, CancellationToken ct = default)
            => Task.FromResult(Result.Success(
                new DeliveryOrderDto(orderId, customerName, plantId, requestedDate, null, "Draft", 0m)));

        public Task<Result> ConfirmOrderAsync(string orderId, CancellationToken ct = default)
            => Task.FromResult(Result.Success());

        public Task<Result> ShipOrderAsync(string orderId, DateTime shippedDate, CancellationToken ct = default)
            => Task.FromResult(orderId == "CONFLICT"
                ? Result.Failure(Error.Conflict("Delivery order can only be shipped from Confirmed status."))
                : Result.Success());

        public Task<Result> CancelOrderAsync(string orderId, CancellationToken ct = default)
            => Task.FromResult(Result.Success());
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "shp-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task GetOrders_returns_200_for_authenticated_reader()
    {
        var res = await Client().GetAsync("/api/v1/shp/orders?plantId=P1");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<DeliveryOrderDto>>();
        rows!.Should().ContainSingle(o => o.OrderId == "DO1" && o.PlantId == "P1");
    }

    [Fact]
    public async Task CreateOrder_without_shp_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/shp/orders",
            new { orderId = "DO2", customerName = "ACME", plantId = "P1", requestedDate = DateTime.UtcNow });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "shp:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task CreateOrder_with_shp_manage_returns_200_with_dto()
    {
        var res = await Client("shp:manage").PostAsJsonAsync("/api/v1/shp/orders",
            new { orderId = "DO2", customerName = "ACME", plantId = "P1", requestedDate = DateTime.UtcNow });
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<DeliveryOrderDto>();
        dto!.OrderId.Should().Be("DO2");
        dto.Status.Should().Be("Draft");
    }

    [Fact]
    public async Task ShipOrder_success_returns_204()
    {
        var res = await Client("shp:manage").PostAsJsonAsync("/api/v1/shp/orders/OK/ship",
            new { shippedDate = DateTime.UtcNow });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 상태전이는 204");
    }

    [Fact]
    public async Task ShipOrder_conflict_maps_to_409()
    {
        var res = await Client("shp:manage").PostAsJsonAsync("/api/v1/shp/orders/CONFLICT/ship",
            new { shippedDate = DateTime.UtcNow });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "상태전이 위반(Conflict)은 409로 매핑");
    }

    [Fact]
    public async Task ConfirmOrder_success_returns_204()
    {
        var res = await Client("shp:manage").PostAsync("/api/v1/shp/orders/DO1/confirm", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CancelOrder_success_returns_204()
    {
        var res = await Client("shp:manage").PostAsync("/api/v1/shp/orders/DO1/cancel", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
