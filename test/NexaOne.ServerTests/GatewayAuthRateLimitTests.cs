using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>"auth" 정책(IP당 10/min)이 통합 호스트에서 동작함을 검증한다 — 기능 테스트와 분리(RateLimiting ON).
/// TestServer는 RemoteIpAddress가 null이라 "anonymous" 파티션을 쓰므로, 이 클래스만 한도를 건드린다.</summary>
public sealed class GatewayAuthRateLimitTests : IClassFixture<GatewayAuthRateLimitTests.RlFactory>
{
    private readonly RlFactory _factory;
    public GatewayAuthRateLimitTests(RlFactory factory) => _factory = factory;

    public sealed class RlFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-auth-rl-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", "phase3b-ratelimit-jwt-secret-key-at-least-32b!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-rl-test");
            builder.UseSetting("Jwt:Audience", "nexaone-rl-test");
            builder.UseSetting("RateLimiting:Enabled", "true");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    [Fact]
    public async Task Auth_endpoint_throttles_after_ten_requests_per_minute()
    {
        var client = _factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 12; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/auth/login",
                new { userId = "rl-ghost", password = "x", plantId = "x" });
            statuses.Add(res.StatusCode);
        }
        statuses.Should().Contain(HttpStatusCode.TooManyRequests,
            "IP당 10/min을 초과하면 429가 반환돼야 한다(\"auth\" 정책)");
    }
}
