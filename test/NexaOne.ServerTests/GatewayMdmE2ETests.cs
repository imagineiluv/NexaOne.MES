using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>게이트웨이 우선 MDM E2E(Phase 2) — modules OFF(게이트웨이는 plugin 무관) + SQLite(NexaMes 스키마 부트스트랩)로
/// /command/MDM.CreatePlant 저장 후 /query/MDM.PlantList 조회 라운드트립을 검증한다. + 권한 미보유 403.</summary>
public sealed class GatewayMdmE2ETests : IClassFixture<GatewayMdmE2ETests.GatewayFactory>
{
    private const string Secret = "phase2-gateway-e2e-jwt-secret-key-at-least-32-bytes-long";
    private const string Issuer = "nexaone-test";
    private readonly GatewayFactory _factory;
    public GatewayMdmE2ETests(GatewayFactory factory) => _factory = factory;

    public sealed class GatewayFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-server-e2e-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
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

    private HttpClient AuthedClient(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "e2e-user") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Command_then_query_roundtrips_plant_via_named_queries()
    {
        var client = AuthedClient("mdm:manage");

        var plantId = "E2E_PLANT_" + Guid.NewGuid().ToString("N")[..8];
        var save = await client.PostAsJsonAsync($"/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        {
            ["plantId"] = plantId,
            ["plantName"] = "E2E 공장",
            ["description"] = "phase2 e2e",
            ["country"] = "KR",
            ["timeZone"] = "Asia/Seoul",
        });
        save.StatusCode.Should().Be(HttpStatusCode.OK, "등록 쓰기쿼리는 mdm:manage 권한으로 성공해야 한다");

        var list = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantList", new Dictionary<string, object> { ["plantId"] = plantId });
        list.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = await list.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().ContainSingle(r => r.ContainsKey("PLANT_ID") && r["PLANT_ID"].ToString() == plantId,
            "저장한 공장이 명명 조회쿼리로 라운드트립돼야 한다");
    }

    [Fact]
    public async Task Command_without_permission_is_forbidden()
    {
        var client = AuthedClient("fdc:read");
        var res = await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        {
            ["plantId"] = "NOPERM", ["plantName"] = "x",
        });
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden, "쓰기쿼리 requiredPermission(mdm:manage) 미보유 시 403");
    }

    [Fact]
    public async Task Enhanced_combo_query_executes_on_sqlite()
    {
        var client = AuthedClient("mdm:manage");
        var plantId = "COMBO_" + Guid.NewGuid().ToString("N")[..8];
        await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object>
        { ["plantId"] = plantId, ["plantName"] = "콤보공장" });

        var res = await client.PostAsJsonAsync("/api/v1/query/MDM.PlantCombo", new Dictionary<string, object>());
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        rows!.Should().Contain(r => r.ContainsKey("VALUE") && r["VALUE"].ToString() == plantId && r.ContainsKey("TEXT"),
            "고도화 콤보 쿼리는 VALUE/TEXT를 SQLite에서 반환해야 한다");
    }

    [Theory]
    [InlineData("MDM.AreaCombo")]
    [InlineData("MDM.AreaList")]
    [InlineData("MDM.ProductCombo")]
    [InlineData("MDM.ProductList")]
    [InlineData("MDM.CodeClassList")]
    [InlineData("MDM.CodeCombo")]
    [InlineData("MDM.EquipmentCombo")]
    [InlineData("MDM.WorkerClassList")]
    [InlineData("MDM.WorkerList")]
    [InlineData("MDM.ShiftList")]
    [InlineData("MDM.WorkCalendarList")]
    [InlineData("MDM.CustomerList")]
    [InlineData("MDM.DeliveryItemList")]
    [InlineData("IVT.MaterialLotList")]
    [InlineData("IVT.MaterialTxList")]
    [InlineData("IVT.IncomingList")]
    [InlineData("IVT.MoveList")]
    [InlineData("IVT.DispensingList")]
    [InlineData("COM.AlarmClassList")]
    [InlineData("COM.AlarmList")]
    [InlineData("COM.StateModelList")]
    [InlineData("COM.StateList")]
    [InlineData("COM.LabelList")]
    [InlineData("COM.IdRuleList")]
    public async Task Enhanced_read_queries_execute_on_sqlite(string queryId)
    {
        var client = AuthedClient("mdm:manage");
        var res = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", new Dictionary<string, object>());
        res.StatusCode.Should().Be(System.Net.HttpStatusCode.OK,
            $"{queryId}는 NexaMes SQLite 스키마에서 유효 SQL이어야 한다(고도화 이식 검증)");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
    }
}
