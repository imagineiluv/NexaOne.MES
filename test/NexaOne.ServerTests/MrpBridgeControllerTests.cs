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
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>MRP v1 브리지 컨트롤러 HTTP 매핑 검증(ADR-008, ServerTests 규약: modules OFF + 가짜 IMrpBridge).
/// 권한 게이트(pom:manage 403/200)·실행자 전달(JWT sub → executedBy)·실패 실행도 200(실행 이력 요약이므로
/// Status=Failed로 본문 전달)을 결정적으로 검증한다. 실모듈 경로(POM MrpPlanningRepository + SQLite 시드)는
/// 부팅 실측(boot-visual)과 MrpCalculatorTests(순수 전개 규칙)가 커버한다.</summary>
public sealed class MrpBridgeControllerTests : IClassFixture<MrpBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-mrp-bridge-e2e-jwt-secret-key-at-least-32b";
    private const string Issuer = "nexaone-mrp-bridge-test";
    private readonly BridgeFactory _factory;
    public MrpBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-mrp-bridge-{Guid.NewGuid():N}.db");
        public readonly FakeBridge Bridge = new();
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
            builder.ConfigureTestServices(s => s.AddSingleton<IMrpBridge>(Bridge));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    // 가짜 브리지 — 마지막 executedBy를 기록하고, NextResult로 성공/실패 실행 요약을 흉내낸다.
    public sealed class FakeBridge : IMrpBridge
    {
        public string? LastExecutedBy;
        public string? LastConvertRunId;
        public MrpRunResult NextResult = new("MRP-TEST-0001", "Success", 2, 3, null);
        public MrpConvertResult NextConvert = new("MRP-TEST-0001", 3, 2, 1, null);

        public Task<MrpRunResult> RunAsync(string executedBy, CancellationToken ct = default)
        {
            LastExecutedBy = executedBy;
            return Task.FromResult(NextResult);
        }

        public Task<MrpConvertResult> ConvertAsync(string? runId, string executedBy, CancellationToken ct = default)
        {
            LastExecutedBy = executedBy;
            LastConvertRunId = runId;
            return Task.FromResult(NextConvert);
        }
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "mrp-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Run_without_pom_manage_is_forbidden()
    {
        var res = await Client("pom:read").PostAsync("/api/v1/pom/mrp/run", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "MRP 실행은 쓰기 성격 — pom:manage 미보유는 403");
    }

    [Fact]
    public async Task Run_unauthenticated_is_unauthorized()
    {
        var res = await _factory.CreateClient().PostAsync("/api/v1/pom/mrp/run", content: null);
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Run_with_pom_manage_returns_200_summary_and_passes_executor()
    {
        _factory.Bridge.NextResult = new("MRP-20260709-0001", "Success", 1, 3, null);

        var res = await Client("pom:manage").PostAsync("/api/v1/pom/mrp/run", content: null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<MrpRunResult>();
        dto!.RunId.Should().Be("MRP-20260709-0001");
        dto.Status.Should().Be("Success");
        dto.DemandCount.Should().Be(1);
        dto.PlannedOrderCount.Should().Be(3);
        _factory.Bridge.LastExecutedBy.Should().Be("mrp-bridge-tester", "JWT sub가 실행자(EXECUTED_BY)로 전달돼야 한다");
    }

    [Fact]
    public async Task Convert_without_pom_manage_is_forbidden()
    {
        var res = await Client("pom:read").PostAsJsonAsync("/api/v1/pom/mrp/convert", new { });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "실오더 전환은 쓰기 — pom:manage 403 게이트");
    }

    [Fact]
    public async Task Convert_with_pom_manage_returns_summary_and_passes_run_id()
    {
        _factory.Bridge.NextConvert = new("MRP-RUN-77", 3, 2, 1, null);

        var res = await Client("pom:manage").PostAsJsonAsync("/api/v1/pom/mrp/convert", new { runId = "MRP-RUN-77" });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<MrpConvertResult>();
        dto!.Converted.Should().Be(3);
        dto.PurchaseOrders.Should().Be(2);
        dto.WorkOrders.Should().Be(1);
        _factory.Bridge.LastConvertRunId.Should().Be("MRP-RUN-77");
        _factory.Bridge.LastExecutedBy.Should().Be("mrp-bridge-tester");
    }

    [Fact]
    public async Task Failed_run_still_returns_200_with_failure_summary()
    {
        // 실패도 '실행 이력 1건'(MRP_RUN Failed) — HTTP 오류가 아니라 요약 본문으로 전달한다(재실행 안전).
        _factory.Bridge.NextResult = new("MRP-20260709-0002", "Failed", 0, 0, "BOM 순환 감지: A → B → A");

        var res = await Client("pom:manage").PostAsync("/api/v1/pom/mrp/run", content: null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await res.Content.ReadFromJsonAsync<MrpRunResult>();
        dto!.Status.Should().Be("Failed");
        dto.Message.Should().Contain("순환");
    }
}
