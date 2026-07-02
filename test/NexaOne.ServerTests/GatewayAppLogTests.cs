using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>DB 앱 로그 슬라이스 E2E — modules OFF + SQLite + AppLogging:Db:Enabled=true. DbLoggerProvider가
/// ILogger Warning+ 항목을 채널→AppLogFlushWorker→SYS_APP_LOG(V064)로 실기록하고, LOG_VIEWER 화면 백엔드
/// (SYS.AppLogList)가 그 행을 반환하는지 로거→테이블→명명쿼리 전 구간을 검증한다(Information 레벨 미기록 포함).</summary>
public sealed class GatewayAppLogTests : IClassFixture<GatewayAppLogTests.AppLogFactory>
{
    private const string Secret = "applog-gateway-e2e-jwt-secret-key-32bytes+!!";
    private const string Issuer = "nexaone-applog-test";
    private readonly AppLogFactory _factory;
    public GatewayAppLogTests(AppLogFactory factory) => _factory = factory;

    public sealed class AppLogFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-applog-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("AppLogging:Db:Enabled", "true");   // DB 로거 게이트 ON — 실기록 검증
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

    private HttpClient AuthedClient()
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(Issuer, Issuer,
            new[] { new Claim(ClaimTypes.NameIdentifier, "applog-e2e-user") },
            expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    [Fact]
    public async Task Warning_log_is_flushed_and_visible_via_named_query_but_information_is_not()
    {
        var marker = $"applog-e2e-{Guid.NewGuid():N}";
        var infoMarker = $"applog-info-{Guid.NewGuid():N}";
        var logger = _factory.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AppLogE2E");
        logger.LogWarning("경고 발생: {Marker}", marker);
        logger.LogInformation("정보 로그: {Marker}", infoMarker);

        // 채널→플러시 워커 경유라 비동기 — 짧게 폴링해 행 도착을 기다린다.
        var client = AuthedClient();
        List<Dictionary<string, object>>? rows = null;
        for (var i = 0; i < 25; i++)
        {
            var res = await client.PostAsJsonAsync("/api/v1/query/SYS.AppLogList", new Dictionary<string, object>());
            res.StatusCode.Should().Be(HttpStatusCode.OK);
            rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
            if (rows!.Any(r => r["MESSAGE"].ToString()!.Contains(marker))) break;
            await Task.Delay(200);
        }

        var logged = rows!.Where(r => r["MESSAGE"].ToString()!.Contains(marker)).ToList();
        logged.Should().NotBeEmpty("Warning 로그는 DbLoggerProvider→플러시 워커→SYS_APP_LOG에 기록돼야 한다(LOG_VIEWER 원천)");
        logged[0]["LOG_LEVEL"].ToString().Should().Be("Warning");
        logged[0]["CATEGORY"].ToString().Should().Be("AppLogE2E");

        rows!.Should().NotContain(r => r["MESSAGE"].ToString()!.Contains(infoMarker),
            "Information 레벨은 기록 대상이 아니다(Warning+만)");
    }
}
