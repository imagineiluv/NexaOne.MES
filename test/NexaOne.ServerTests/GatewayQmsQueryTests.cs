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

/// <summary>게이트웨이 우선 QMS 기준정보 read E2E — modules OFF + SQLite(NexaMes 스키마 부트스트랩, V037 포함).
/// QMS_INSPECTION_ITEM / QMS_INSPECTION_DEF / QMS_INCOMING_INSP_METHOD 를 SqliteConnection 직접 INSERT로 시드한 뒤
/// 점등용 신규 NULL-guard 전체조회 쿼리(InspectionItemList/InspectionDefList/IncomingInspMethodList) 라운드트립을 검증한다. + 미인증 401.</summary>
public sealed class GatewayQmsQueryTests : IClassFixture<GatewayQmsQueryTests.QmsFactory>
{
    private const string Secret = "qms-std-gateway-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-qms-test";
    private readonly QmsFactory _factory;
    public GatewayQmsQueryTests(QmsFactory factory) => _factory = factory;

    public sealed class QmsFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-qms-e2e-{Guid.NewGuid():N}.db");
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
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "qms-e2e-user") };
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];
    private static string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");

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
        var client = _factory.CreateClient(); // 토큰 없음
        var res = await client.PostAsJsonAsync("/api/v1/query/QMS.InspectionItemList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task InspectionItemList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"II_{Suffix()}";
        Exec(@"INSERT INTO QMS_INSPECTION_ITEM (ITEM_ID, ITEM_NAME, INSPECTION_TYPE, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, '외관검사', 'Incoming', 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@now", Now());
        });

        var rows = await Query("QMS.InspectionItemList");
        rows.Select(r => r["ITEM_ID"].ToString()).Should().Contain(id, "V037 검사항목이 전체조회돼야 한다");
    }

    [Fact]
    public async Task InspectionDefList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"ID_{Suffix()}";
        Exec(@"INSERT INTO QMS_INSPECTION_DEF (INSP_DEF_ID, INSP_DEF_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, '수입검사정의', 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@now", Now());
        });

        var rows = await Query("QMS.InspectionDefList");
        rows.Select(r => r["INSP_DEF_ID"].ToString()).Should().Contain(id, "V037 검사정의가 전체조회돼야 한다");
    }

    [Fact]
    public async Task IncomingInspMethodList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"IM_{Suffix()}";
        Exec(@"INSERT INTO QMS_INCOMING_INSP_METHOD (METHOD_ID, METHOD_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, 'AQL 1.0 정상검사', 1, 'TEST', @now, 'TEST', @now)", cmd =>
        {
            cmd.Parameters.AddWithValue("@id", id);
            cmd.Parameters.AddWithValue("@now", Now());
        });

        var rows = await Query("QMS.IncomingInspMethodList");
        rows.Select(r => r["METHOD_ID"].ToString()).Should().Contain(id, "V037 수입검사 방법이 전체조회돼야 한다");
    }

    [Fact]
    public async Task GaugeList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"G_{Suffix()}";
        Exec(@"INSERT INTO QMS_GAUGE (GAUGE_ID, GAUGE_NAME, IS_ACTIVE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, '버니어캘리퍼스', 1, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@now", Now()); });
        var rows = await Query("QMS.GaugeList");
        rows.Select(r => r["GAUGE_ID"].ToString()).Should().Contain(id, "V038 계측기가 전체조회돼야 한다");
    }

    [Fact]
    public async Task GaugeCalibrationPlanList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"CP_{Suffix()}";
        Exec(@"INSERT INTO QMS_GAUGE_CALIBRATION_PLAN (PLAN_ID, GAUGE_ID, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, @g, 'Planned', 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@g", "G_" + Suffix()); cmd.Parameters.AddWithValue("@now", Now()); });
        var rows = await Query("QMS.GaugeCalibrationPlanList");
        rows.Select(r => r["PLAN_ID"].ToString()).Should().Contain(id, "V038 검교정 계획이 전체조회돼야 한다");
    }

    [Fact]
    public async Task GaugeCalibrationResultList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"CR_{Suffix()}";
        Exec(@"INSERT INTO QMS_GAUGE_CALIBRATION_RESULT (RESULT_ID, GAUGE_ID, CALIBRATED_AT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, @g, @now, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@g", "G_" + Suffix()); cmd.Parameters.AddWithValue("@now", Now()); });
        var rows = await Query("QMS.GaugeCalibrationResultList");
        rows.Select(r => r["RESULT_ID"].ToString()).Should().Contain(id, "V038 검교정 내역이 전체조회돼야 한다");
    }

    [Fact]
    public async Task GaugeRnrPlanList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"RP_{Suffix()}";
        Exec(@"INSERT INTO QMS_GAUGE_RNR_PLAN (RNR_PLAN_ID, GAUGE_ID, STATUS, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, @g, 'Planned', 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@g", "G_" + Suffix()); cmd.Parameters.AddWithValue("@now", Now()); });
        var rows = await Query("QMS.GaugeRnrPlanList");
        rows.Select(r => r["RNR_PLAN_ID"].ToString()).Should().Contain(id, "V038 RNR 계획이 전체조회돼야 한다");
    }

    [Fact]
    public async Task GaugeRnrResultList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"RR_{Suffix()}";
        Exec(@"INSERT INTO QMS_GAUGE_RNR_RESULT (RNR_RESULT_ID, GAUGE_ID, EVALUATED_AT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, @g, @now, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@g", "G_" + Suffix()); cmd.Parameters.AddWithValue("@now", Now()); });
        var rows = await Query("QMS.GaugeRnrResultList");
        rows.Select(r => r["RNR_RESULT_ID"].ToString()).Should().Contain(id, "V038 RNR 평가가 전체조회돼야 한다");
    }

    [Fact]
    public async Task GaugeRepairResultList_returns_all()
    {
        EnsureSchemaReady();
        var id = $"RE_{Suffix()}";
        Exec(@"INSERT INTO QMS_GAUGE_REPAIR_RESULT (REPAIR_ID, GAUGE_ID, REPAIRED_AT, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES (@id, @g, @now, 'TEST', @now, 'TEST', @now)", cmd =>
        { cmd.Parameters.AddWithValue("@id", id); cmd.Parameters.AddWithValue("@g", "G_" + Suffix()); cmd.Parameters.AddWithValue("@now", Now()); });
        var rows = await Query("QMS.GaugeRepairResultList");
        rows.Select(r => r["REPAIR_ID"].ToString()).Should().Contain(id, "V038 수리 내역이 전체조회돼야 한다");
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId)
    {
        var res = await AuthedClient().PostAsJsonAsync($"/api/v1/query/{queryId}", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
