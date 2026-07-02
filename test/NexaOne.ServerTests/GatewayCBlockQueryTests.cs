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

/// <summary>게이트웨이 우선 (c)블록 포팅 read E2E — modules OFF + SQLite. 벤더(V059 MDM_VENDOR/ITEM)·작업지시(V060
/// POM_WORK_ORDER)·액션(V061 COM_ACTION/ALARM_ACTION)·배포파일(V012 재사용 SYS.DeployFileList)을 직접 시드한 뒤
/// 명명 read 쿼리 라운드트립을 검증한다(벤더/W_O/알람액션/파일 관리 점등 백엔드). + 미인증 401.</summary>
public sealed class GatewayCBlockQueryTests : IClassFixture<GatewayCBlockQueryTests.CbFactory>
{
    private const string Secret = "cblock-gateway-e2e-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-cblock-test";
    private readonly CbFactory _factory;
    public GatewayCBlockQueryTests(CbFactory factory) => _factory = factory;

    public sealed class CbFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-cblock-{Guid.NewGuid():N}.db");
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
            new[] { new Claim(ClaimTypes.NameIdentifier, "cblock-e2e-user") },
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
        var res = await client.PostAsJsonAsync("/api/v1/query/MDM.VendorList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task VendorList_and_VendorItemList_roundtrip()
    {
        EnsureSchemaReady();
        var vendor = $"VEN_{Suffix()}";
        var item = $"VI_{Suffix()}";
        Exec("INSERT INTO MDM_VENDOR (VENDOR_ID, VENDOR_NAME, VENDOR_TYPE, IS_ACTIVE) VALUES (@id, '자재 공급사', 'Material', 1)",
            cmd => cmd.Parameters.AddWithValue("@id", vendor));
        Exec("INSERT INTO MDM_VENDOR_ITEM (VENDOR_ITEM_ID, VENDOR_ID, PRODUCT_ID, LEAD_TIME_DAYS, MOQ, BASE_PRICE, IS_ACTIVE) VALUES (@id, @ven, 'ITEM03', 7, 100, 1500, 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", item); cmd.Parameters.AddWithValue("@ven", vendor); });

        var vendors = await Query("MDM.VendorList", new());
        vendors.Select(r => r["VENDOR_ID"].ToString()).Should().Contain(vendor, "벤더가 조회돼야 한다(벤더 관리 점등)");

        var items = await Query("MDM.VendorItemList", new() { ["vendorId"] = vendor });
        items.Select(r => r["VENDOR_ITEM_ID"].ToString()).Should().Contain(item);
        items.Should().OnlyContain(r => r["VENDOR_ID"].ToString() == vendor, "vendorId 필터는 해당 벤더 품목만(벤더 품목 점등)");
    }

    [Fact]
    public async Task WorkOrderList_returns_seeded_and_status_filter_narrows()
    {
        EnsureSchemaReady();
        var plant = "P_" + Suffix();
        var started = $"WO_{Suffix()}";
        var created = $"WO_{Suffix()}";
        Exec("INSERT INTO POM_WORK_ORDER (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCT_ID, PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY, STATUS, IS_HOLD) VALUES (@id, @plant, 'W/O', 'ITEM01', 100, 50, 0, 0, 'Started', 'N')",
            cmd => { cmd.Parameters.AddWithValue("@id", started); cmd.Parameters.AddWithValue("@plant", plant); });
        Exec("INSERT INTO POM_WORK_ORDER (WORK_ORDER_ID, PLANT_ID, WORK_ORDER_NAME, PRODUCT_ID, PLAN_QTY, START_QTY, COMPLETE_QTY, SCRAP_QTY, STATUS, IS_HOLD) VALUES (@id, @plant, 'W/O', 'ITEM01', 200, 0, 0, 0, 'Created', 'N')",
            cmd => { cmd.Parameters.AddWithValue("@id", created); cmd.Parameters.AddWithValue("@plant", plant); });

        var all = await Query("POM.WorkOrderList", new() { ["plantId"] = plant });
        all.Select(r => r["WORK_ORDER_ID"].ToString()).Should().Contain(new[] { started, created }, "공장 작업지시가 조회돼야 한다(W/O 관리 점등)");

        var startedOnly = await Query("POM.WorkOrderList", new() { ["plantId"] = plant, ["status"] = "Started" });
        var ids = startedOnly.Select(r => r["WORK_ORDER_ID"].ToString()).ToList();
        ids.Should().Contain(started);
        ids.Should().NotContain(created, "status 필터는 해당 상태만 반환");
    }

    [Fact]
    public async Task ActionList_and_AlarmActionList_roundtrip()
    {
        EnsureSchemaReady();
        var action = $"ACT_{Suffix()}";
        var map = $"AA_{Suffix()}";
        var alarm = $"ALM_{Suffix()}";
        Exec("INSERT INTO COM_ACTION (ACTION_ID, ACTION_NAME, ACTION_TYPE, EMAIL_TITLE, IS_ACTIVE) VALUES (@id, '메일 발송', 'Email', '알람 발생', 1)",
            cmd => cmd.Parameters.AddWithValue("@id", action));
        Exec("INSERT INTO COM_ALARM_ACTION (ALARM_ACTION_ID, ALARM_ID, ACTION_ID, ACTION_SEQUENCE, IS_ACTIVE) VALUES (@id, @alarm, @act, 1, 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", map); cmd.Parameters.AddWithValue("@alarm", alarm); cmd.Parameters.AddWithValue("@act", action); });

        var actions = await Query("COM.ActionList", new());
        actions.Select(r => r["ACTION_ID"].ToString()).Should().Contain(action, "액션 정의가 조회돼야 한다(알람 액션 관리 점등)");

        var maps = await Query("COM.AlarmActionList", new() { ["alarmId"] = alarm });
        maps.Select(r => r["ALARM_ACTION_ID"].ToString()).Should().Contain(map);
        maps.Should().OnlyContain(r => r["ALARM_ID"].ToString() == alarm, "alarmId 필터는 해당 알람 매핑만(알람별 액션 점등)");
    }

    [Fact]
    public async Task DeployFileList_returns_seeded()
    {
        EnsureSchemaReady();
        var file = Guid.NewGuid().ToString("N");
        Exec("INSERT INTO SYS_DEPLOY_FILE (FILE_ID, VERSION, FILE_NAME, HASH, FILE_SIZE, DESCRIPTION, FORCE_UPDATE, IS_ACTIVE, UPLOADED_BY, UPLOADED_AT) VALUES (@id, @ver, 'client.zip', 'abc123', 1024, '릴리스', 0, 1, 'admin', '2026-06-01 00:00:00')",
            cmd => { cmd.Parameters.AddWithValue("@id", file); cmd.Parameters.AddWithValue("@ver", $"9.{Random.Shared.Next(1000)}.0"); });

        var rows = await Query("SYS.DeployFileList", new());
        rows.Select(r => r["FILE_ID"].ToString()).Should().Contain(file, "배포 파일 메타가 조회돼야 한다(파일 관리 점등)");
        rows.Should().OnlyContain(r => r.ContainsKey("VERSION") && r.ContainsKey("FILE_NAME"));
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
