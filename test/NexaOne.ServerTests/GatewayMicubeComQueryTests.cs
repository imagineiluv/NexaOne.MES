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

/// <summary>게이트웨이 우선 MICUBE→COM 이관 read E2E — modules OFF + SQLite. 메일서버/메일수신자(일반·알람)/서비스
/// (V057 COM_MAIL_SERVER/COM_MAIL_RECIPIENT/COM_SERVICE)를 직접 시드한 뒤 명명 read 쿼리(COM.MailServerList/
/// MailRecipientList/AlarmMailRecipientList/ServiceList) 라운드트립을 검증한다. MAIL_TYPE 분기(일반/알람) 포함. + 미인증 401.</summary>
public sealed class GatewayMicubeComQueryTests : IClassFixture<GatewayMicubeComQueryTests.McFactory>
{
    private const string Secret = "micube-com-gateway-e2e-jwt-secret-32bytes+!";
    private const string Issuer = "nexaone-micube-com-test";
    private readonly McFactory _factory;
    public GatewayMicubeComQueryTests(McFactory factory) => _factory = factory;

    public sealed class McFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-micube-com-{Guid.NewGuid():N}.db");
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
            new[] { new Claim(ClaimTypes.NameIdentifier, "micube-com-user"),
                new Claim(NexaOne.Common.Security.Permissions.ClaimType, "com:manage") },
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

    private void SeedRecipient(string recipientId, string userId, string mailType)
        => Exec("INSERT INTO COM_MAIL_RECIPIENT (RECIPIENT_ID, PLANT_ID, USER_ID, EQUIPMENT_ID, MAIL_ADDRESS, MAIL_TYPE, IS_ACTIVE) VALUES (@id, 'PLANT01', @user, 'EQ01', @user || '@x.com', @type, 1)",
            cmd => { cmd.Parameters.AddWithValue("@id", recipientId); cmd.Parameters.AddWithValue("@user", userId); cmd.Parameters.AddWithValue("@type", mailType); });

    [Fact]
    public async Task Unauthenticated_query_is_unauthorized()
    {
        EnsureSchemaReady();
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/v1/query/COM.MailServerList", new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "read 게이트웨이도 인증은 요구한다");
    }

    [Fact]
    public async Task MailServerList_and_ServiceList_return_seeded()
    {
        EnsureSchemaReady();
        var srv = $"SRV_{Suffix()}";
        var svc = $"SVC_{Suffix()}";
        Exec("INSERT INTO COM_MAIL_SERVER (SERVER_ID, SERVER_NAME, HOST, PORT, SENDER_ADDRESS, USE_SSL, IS_ACTIVE) VALUES (@id, '기본 SMTP', 'smtp.x.com', 587, 'noreply@x.com', 'Y', 1)",
            cmd => cmd.Parameters.AddWithValue("@id", srv));
        Exec("INSERT INTO COM_SERVICE (SERVICE_ID, SERVICE_NAME, SERVICE_TYPE, STATUS, IS_ACTIVE) VALUES (@id, '알람 수집 서비스', 'Collector', 'Running', 1)",
            cmd => cmd.Parameters.AddWithValue("@id", svc));

        var servers = await Query("COM.MailServerList", new());
        servers.Select(r => r["SERVER_ID"].ToString()).Should().Contain(srv, "메일 서버가 조회돼야 한다(메일 서버 관리 점등)");

        var services = await Query("COM.ServiceList", new());
        services.Select(r => r["SERVICE_ID"].ToString()).Should().Contain(svc, "서비스가 조회돼야 한다(서비스 관리 점등)");
    }

    [Fact]
    public async Task Recipient_lists_split_by_mail_type()
    {
        EnsureSchemaReady();
        var mail = $"RC_{Suffix()}";
        var alarm = $"RC_{Suffix()}";
        SeedRecipient(mail, "user_mail", "Mail");
        SeedRecipient(alarm, "user_alarm", "Alarm");

        var general = await Query("COM.MailRecipientList", new());
        general.Select(r => r["RECIPIENT_ID"].ToString()).Should().Contain(mail);
        general.Should().OnlyContain(r => r["MAIL_TYPE"].ToString() == "Mail", "일반 메일 수신자만(설비 메일링/메일 매핑 점등)");

        var alarmOnly = await Query("COM.AlarmMailRecipientList", new());
        alarmOnly.Select(r => r["RECIPIENT_ID"].ToString()).Should().Contain(alarm);
        alarmOnly.Should().OnlyContain(r => r["MAIL_TYPE"].ToString() == "Alarm", "알람 메일 수신자만(알람메일 매핑 점등)");
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
