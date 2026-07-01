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

/// <summary>게이트웨이 우선 FACTORY_STD 라벨 read E2E — modules OFF + SQLite. 레거시 STD_TB_LABEL* 를 V054(MDM_LABEL*)로
/// 포팅한 마스터/발행이력/매핑을 직접 시드한 뒤 명명 read 쿼리(MDM.Label*List) 라운드트립을 검증한다
/// (라벨 마스터/발행이력/매핑 점등 백엔드). + 미인증 401.</summary>
public sealed class GatewayStdLabelQueryTests : IClassFixture<GatewayStdLabelQueryTests.LabelFactory>
{
    private const string Secret = "std-label-gateway-e2e-jwt-secret-key-32b+!!";
    private const string Issuer = "nexaone-label-test";
    private readonly LabelFactory _factory;
    public GatewayStdLabelQueryTests(LabelFactory factory) => _factory = factory;

    public sealed class LabelFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-label-e2e-{Guid.NewGuid():N}.db");
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
            new[] { new Claim(ClaimTypes.NameIdentifier, "label-e2e-user") },
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

    private void SeedLabel(string labelId, string plantId, string name)
        => Exec("INSERT INTO MDM_LABEL (LABEL_ID, PLANT_ID, LABEL_NAME, IS_ACTIVE) VALUES (@id, @plant, @name, 1)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", labelId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@name", name);
        });

    private void SeedIssue(string issueId, string plantId, string labelId, int printCnt)
        => Exec(@"INSERT INTO MDM_LABEL_ISSUE (ISSUE_ID, PLANT_ID, LABEL_ID, ITEM_ID, LOT_ID, SERIAL_NUM, PRINT_CNT, ISSUED_AT)
                  VALUES (@id, @plant, @label, 'ITEM01', 'LOT01', 'SN001', @cnt, '2026-06-01 00:00:00')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", issueId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@label", labelId);
            cmd.Parameters.AddWithValue("@cnt", printCnt);
        });

    private void SeedMapping(string mappingId, string plantId, string labelId)
        => Exec(@"INSERT INTO MDM_LABEL_MAPPING (MAPPING_ID, PLANT_ID, PROCESS_ID, ITEM_ID, LABEL_ID, PRINT_LIMIT_CNT, PRINT_LIMIT_YN)
                  VALUES (@id, @plant, 'PROC1', 'ITEM01', @label, 3, 'Y')", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", mappingId);
            cmd.Parameters.AddWithValue("@plant", plantId);
            cmd.Parameters.AddWithValue("@label", labelId);
        });

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/MDM.LabelList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task LabelList_returns_seeded_and_plant_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var a = $"LB_{Suffix()}";
        var b = $"LB_{Suffix()}";
        SeedLabel(a, plant, "제품 라벨");
        SeedLabel(b, plant, "박스 라벨");

        var rows = await Query("MDM.LabelList", new() { ["plantId"] = plant });
        rows.Select(r => r["LABEL_ID"].ToString()).Should().Contain(new[] { a, b }, "공장 라벨이 조회돼야 한다(라벨 마스터 점등)");
        rows.Should().OnlyContain(r => r.ContainsKey("LABEL_NAME"));
    }

    [Fact]
    public async Task LabelIssueList_label_filter_narrows()
    {
        EnsureSchemaReady();
        var label = $"LB_{Suffix()}";
        var mine = $"IS_{Suffix()}";
        SeedLabel(label, "P_" + Suffix(), "제품 라벨");
        SeedIssue(mine, "P1", label, 5);
        SeedIssue($"IS_{Suffix()}", "P1", $"LB_{Suffix()}", 3); // 다른 라벨 발행 — 제외

        var rows = await Query("MDM.LabelIssueList", new() { ["labelId"] = label });
        var ids = rows.Select(r => r["ISSUE_ID"].ToString()).ToList();
        ids.Should().Contain(mine);
        rows.Should().OnlyContain(r => r["LABEL_ID"].ToString() == label, "labelId 필터는 해당 라벨 발행만(발행이력 점등)");
    }

    [Fact]
    public async Task LabelMappingList_returns_seeded()
    {
        EnsureSchemaReady();
        var label = $"LB_{Suffix()}";
        var map = $"MP_{Suffix()}";
        SeedLabel(label, "P_" + Suffix(), "제품 라벨");
        SeedMapping(map, "P1", label);

        var rows = await Query("MDM.LabelMappingList", new() { ["labelId"] = label });
        rows.Select(r => r["MAPPING_ID"].ToString()).Should().Contain(map, "라벨 매핑이 조회돼야 한다(매핑 관리 점등)");
        rows.Should().OnlyContain(r => r.ContainsKey("PRINT_LIMIT_YN"));
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
