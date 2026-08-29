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

/// <summary>게이트웨이 우선 EPT_STD(설비성능 표준) read E2E — modules OFF + SQLite. 레거시 EPT_TB_LAYOUT /
/// EPT_TB_EQUIPMENT_EPT_PROPERTY를 V055(EST_EPT_LAYOUT/EST_EPT_EQUIPMENT_PROPERTY)로 포팅한 마스터를 직접 시드한 뒤
/// 명명 read 쿼리(EST.LayoutList/EST.EquipmentPropertyList) 라운드트립을 검증한다(레이아웃 관리·설비 EPT 속성 점등 백엔드). + 미인증 401.</summary>
public sealed class GatewayEptStdQueryTests : IClassFixture<GatewayEptStdQueryTests.EptFactory>
{
    private const string Secret = "ept-std-gateway-e2e-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-ept-test";
    private readonly EptFactory _factory;
    public GatewayEptStdQueryTests(EptFactory factory) => _factory = factory;

    public sealed class EptFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-ept-e2e-{Guid.NewGuid():N}.db");
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

    private void EnsureSchemaReady() => _ = _factory.CreateClient();

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Issuer,
            new[] { new Claim(ClaimTypes.NameIdentifier, "ept-e2e-user"),
                new Claim(NexaOne.Common.Security.Permissions.ClaimType, "est:read") },
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    private void Exec(string sql, Action<SqliteCommand> bind)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        bind(cmd);
        cmd.ExecuteNonQuery();
    }

    private void SeedLayout(string layoutId, string plantId, string name, string areaId)
        => Exec("INSERT INTO EST_EPT_LAYOUT (LAYOUT_ID, PLANT_ID, LAYOUT_NAME, AREA_ID, WIDTH, HEIGHT, IS_ACTIVE) VALUES (@id, @plant, @name, @area, 800, 600, 1)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", layoutId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@area", areaId);
        });

    private void SeedProperty(string equipmentId, string plantId, decimal cycleTime)
        => Exec("INSERT INTO EST_EPT_EQUIPMENT_PROPERTY (EQUIPMENT_ID, PLANT_ID, DESCRIPTION, CYCLE_TIME, DO_MCC, IS_ACTIVE) VALUES (@eq, @plant, 'EPT 속성', @ct, 'Y', 1)", cmd =>
        {
            cmd.Parameters.AddWithValue("@eq", equipmentId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@ct", cycleTime);
        });

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/EST.LayoutList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task LayoutList_returns_seeded_and_area_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var a1 = $"LO_{Suffix()}";
        var a2 = $"LO_{Suffix()}";
        SeedLayout(a1, plant, "조립동 레이아웃", "AREA_A");
        SeedLayout(a2, plant, "가공동 레이아웃", "AREA_B");

        var all = await Query("EST.LayoutList", new() { ["plantId"] = plant });
        all.Select(r => r["LAYOUT_ID"].ToString()).Should().Contain(new[] { a1, a2 }, "공장 레이아웃이 조회돼야 한다(레이아웃 관리 점등)");

        var areaA = await Query("EST.LayoutList", new() { ["plantId"] = plant, ["areaId"] = "AREA_A" });
        var ids = areaA.Select(r => r["LAYOUT_ID"].ToString()).ToList();
        ids.Should().Contain(a1);
        ids.Should().NotContain(a2, "areaId 필터는 해당 구역 레이아웃만 반환");
    }

    [Fact]
    public async Task EquipmentPropertyList_returns_seeded_and_equipment_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var eqA = "EQ_" + Suffix();
        var eqB = "EQ_" + Suffix();
        SeedProperty(eqA, plant, 30m);
        SeedProperty(eqB, plant, 45m);

        var all = await Query("EST.EquipmentPropertyList", new() { ["plantId"] = plant });
        all.Select(r => r["EQUIPMENT_ID"].ToString()).Should().Contain(new[] { eqA, eqB }, "설비 EPT 속성이 조회돼야 한다(설비 EPT 속성 점등)");
        all.Should().OnlyContain(r => r.ContainsKey("CYCLE_TIME"));

        var one = await Query("EST.EquipmentPropertyList", new() { ["equipmentId"] = eqA });
        var ids = one.Select(r => r["EQUIPMENT_ID"].ToString()).ToList();
        ids.Should().Contain(eqA);
        ids.Should().NotContain(eqB, "equipmentId 필터는 해당 설비만 반환");
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> p)
    {
        var res = await AuthedClient().PostAsJsonAsync($"/api/v1/query/{queryId}", p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
