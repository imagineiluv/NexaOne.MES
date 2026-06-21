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
using NexaOne.ServiceContracts.Qms;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>QMS 얇은 브리지 컨트롤러 HTTP 매핑 검증(ADR-008) — modules OFF + 가짜 IQmsBridge 주입으로
/// 읽기 200·쓰기 권한(403/204)·상태전이(Conflict→409·Validation→400)를 Spring/ALC 없이 결정적으로 검증한다.</summary>
public sealed class QmsBridgeControllerTests : IClassFixture<QmsBridgeControllerTests.BridgeFactory>
{
    private const string Secret = "phase-qms-bridge-e2e-jwt-secret-key-at-least-32b!!";
    private const string Issuer = "nexaone-qms-bridge-test";
    private readonly BridgeFactory _factory;
    public QmsBridgeControllerTests(BridgeFactory factory) => _factory = factory;

    public sealed class BridgeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-qms-bridge-{Guid.NewGuid():N}.db");
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
            builder.ConfigureTestServices(s => s.AddSingleton<IQmsBridge>(new FakeBridge()));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private sealed class FakeBridge : IQmsBridge
    {
        public Task<IReadOnlyList<DefectDto>> GetDefectsByLotAsync(string lotId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DefectDto>>(
                new[] { new DefectDto("DF1", lotId, "EQ1", "DC1", 3, 0.05m, DateTime.UtcNow, "insp1", false, null, null) });

        public Task<Result> ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default)
            => Task.FromResult(defectId == "CONFLICT"
                ? Result.Failure(Error.Conflict("Defect is already confirmed."))
                : Result.Success());

        public Task<Result> UpdateControlLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default)
            => Task.FromResult(ucl <= lcl
                ? Result.Failure(Error.Validation(nameof(ucl), "UCL must be greater than LCL."))
                : Result.Success());
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "qms-bridge-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task GetDefects_returns_200_for_authenticated_reader()
    {
        var res = await Client().GetAsync("/api/v1/qms/defects?lotId=LOT1");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<DefectDto>>();
        rows!.Should().ContainSingle(d => d.DefectId == "DF1" && d.LotId == "LOT1");
    }

    [Fact]
    public async Task ConfirmDefect_without_qms_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/qms/defects/DF1/confirm",
            new { confirmerId = "qa1" });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "qms:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task ConfirmDefect_success_returns_204()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/defects/DF1/confirm",
            new { confirmerId = "qa1" });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 상태전이는 204");
    }

    [Fact]
    public async Task ConfirmDefect_conflict_maps_to_409()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/defects/CONFLICT/confirm",
            new { confirmerId = "qa1" });
        res.StatusCode.Should().Be(HttpStatusCode.Conflict, "재확정(Conflict)은 409로 매핑");
    }

    [Fact]
    public async Task UpdateControlLimits_without_qms_manage_is_forbidden()
    {
        var res = await Client("fdc:read").PostAsJsonAsync("/api/v1/qms/spc-params/SP1/control-limits",
            new { mean = 10.0m, ucl = 12.0m, lcl = 8.0m });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "qms:manage 미보유 쓰기는 403");
    }

    [Fact]
    public async Task UpdateControlLimits_success_returns_204()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/spc-params/SP1/control-limits",
            new { mean = 10.0m, ucl = 12.0m, lcl = 8.0m });
        res.StatusCode.Should().Be(HttpStatusCode.NoContent, "성공 갱신은 204");
    }

    [Fact]
    public async Task UpdateControlLimits_invalid_limits_maps_to_400()
    {
        var res = await Client("qms:manage").PostAsJsonAsync("/api/v1/qms/spc-params/SP1/control-limits",
            new { mean = 10.0m, ucl = 8.0m, lcl = 12.0m });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "UCL<=LCL 불변식 위반(Validation)은 400으로 매핑");
    }
}
