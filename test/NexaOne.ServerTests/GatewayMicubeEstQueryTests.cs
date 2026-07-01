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

/// <summary>게이트웨이 우선 MICUBE→EST 이관 read E2E — modules OFF + SQLite. 설비 상태 매트릭스(기존 EST_STATE_MATRIX,
/// V025) + 설비 이벤트/상태 매핑(V056 EST_EQUIPMENT_EVENT/EST_STATE_ALARM_MAP/EST_STATE_EVENT_MAP)을 직접 시드한 뒤
/// 명명 read 쿼리(EST.StateMatrixList/EquipmentEventList/StateAlarmMapList/StateEventMapList) 라운드트립을 검증한다. + 미인증 401.</summary>
public sealed class GatewayMicubeEstQueryTests : IClassFixture<GatewayMicubeEstQueryTests.McFactory>
{
    private const string Secret = "micube-est-gateway-e2e-jwt-secret-32bytes+!";
    private const string Issuer = "nexaone-micube-est-test";
    private readonly McFactory _factory;
    public GatewayMicubeEstQueryTests(McFactory factory) => _factory = factory;

    public sealed class McFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-micube-est-{Guid.NewGuid():N}.db");
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
            new[] { new Claim(ClaimTypes.NameIdentifier, "micube-est-user") },
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

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/query/EST.EquipmentEventList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task StateMatrixList_returns_seeded_matrix()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        Exec("INSERT INTO EST_STATE_MATRIX (PLANT_ID, FROM_STATE_ID, TO_STATE_ID, ALLOW_FLAG, REQUIRE_REASON, VALID_STATE) VALUES (@p, 'RUN', 'DOWN', 'Y', 'Y', 'Valid')",
            cmd => cmd.Parameters.AddWithValue("@p", plant));

        var rows = await Query("EST.StateMatrixList", new() { ["plantId"] = plant });
        rows.Should().ContainSingle("공장 상태전이 1건이 조회돼야 한다(설비 상태 매트릭스 점등)");
        rows[0]["FROM_STATE_ID"].ToString().Should().Be("RUN");
        rows[0]["ALLOW_FLAG"].ToString().Should().Be("Y");
    }

    [Fact]
    public async Task EquipmentEventList_and_equipment_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var eq = "EQ_" + Suffix();
        var evt = $"EV_{Suffix()}";
        Exec("INSERT INTO EST_EQUIPMENT_EVENT (EVENT_ID, PLANT_ID, EVENT_NAME, EQUIPMENT_ID, EVENT_TYPE, IS_ACTIVE) VALUES (@id, @p, '도어 열림', @eq, 'Safety', 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", evt); cmd.Parameters.AddWithValue("@p", plant); cmd.Parameters.AddWithValue("@eq", eq); });

        var byPlant = await Query("EST.EquipmentEventList", new() { ["plantId"] = plant });
        byPlant.Select(r => r["EVENT_ID"].ToString()).Should().Contain(evt, "설비 이벤트가 조회돼야 한다(설비 이벤트 관리 점등)");

        var byEq = await Query("EST.EquipmentEventList", new() { ["equipmentId"] = eq });
        byEq.Should().OnlyContain(r => r["EQUIPMENT_ID"].ToString() == eq, "equipmentId 필터는 해당 설비만 반환");
    }

    [Fact]
    public async Task StateAlarmMap_and_StateEventMap_roundtrip()
    {
        EnsureSchemaReady();
        var eq = "EQ_" + Suffix();
        var am = $"AM_{Suffix()}";
        var em = $"EM_{Suffix()}";
        Exec("INSERT INTO EST_STATE_ALARM_MAP (MAP_ID, PLANT_ID, EQUIPMENT_ID, ALARM_DEF_ID, SET_STATE, IS_ACTIVE) VALUES (@id, 'P1', @eq, 'ALM01', 'DOWN', 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", am); cmd.Parameters.AddWithValue("@eq", eq); });
        Exec("INSERT INTO EST_STATE_EVENT_MAP (MAP_ID, PLANT_ID, EQUIPMENT_ID, EVENT_ID, SET_STATE, IS_ACTIVE) VALUES (@id, 'P1', @eq, 'EV01', 'IDLE', 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", em); cmd.Parameters.AddWithValue("@eq", eq); });

        var alarm = await Query("EST.StateAlarmMapList", new() { ["equipmentId"] = eq });
        alarm.Select(r => r["MAP_ID"].ToString()).Should().Contain(am, "알람-상태 매핑 점등");
        alarm.Should().OnlyContain(r => r.ContainsKey("ALARM_DEF_ID") && r.ContainsKey("SET_STATE"));

        var evt = await Query("EST.StateEventMapList", new() { ["equipmentId"] = eq });
        evt.Select(r => r["MAP_ID"].ToString()).Should().Contain(em, "이벤트-상태 매핑 점등");
        evt.Should().OnlyContain(r => r.ContainsKey("EVENT_ID"));
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
