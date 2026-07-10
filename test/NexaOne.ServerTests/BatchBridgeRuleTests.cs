using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using NexaOne.ServiceContracts.Pom;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>배치 브리지 룰(MRP v2 스펙 백로그 ④) — BATCH_RULE='bridge:pom.mrp.run'이 명명쿼리가 아니라
/// 등록된 IMrpBridge를 호출함을 수동 실행 API로 검증한다(가짜 브리지 — 모듈 OFF 규약).
/// 미지 브리지 키는 실패로 보고(무음 스킵 금지)한다.</summary>
public sealed class BatchBridgeRuleTests : IClassFixture<BatchBridgeRuleTests.BridgeBatchFactory>
{
    private const string Secret = "batch-bridge-rule-test-jwt-secret-32bytes!!";
    private const string Issuer = "nexaone-batch-bridge-test";
    private readonly BridgeBatchFactory _factory;
    public BatchBridgeRuleTests(BridgeBatchFactory factory) => _factory = factory;

    public sealed class BridgeBatchFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-batch-bridge-{Guid.NewGuid():N}.db");
        public readonly MrpBridgeControllerTests.FakeBridge Bridge = new();
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

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "batch-bridge-tester"),
            new(NexaOne.Common.Security.Permissions.ClaimType, "sys:manage"),
        };
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private void SeedBatch(string batchId, string rule, string inputData = "{}")
    {
        using var conn = new SqliteConnection($"Data Source={_factory.DbPath};Foreign Keys=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_BATCH_PROCESS
            (BATCH_ID, BATCH_NAME, BATCH_TYPE, BATCH_RULE, BATCH_OPTIONS, BATCH_INPUTDATA, DESCRIPTION,
             VALID_STATE, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES (@id, @id, 'Interval', @rule, '86400', @input, 'bridge rule test',
                    'Valid', 'SYSTEM', @now, 'SYSTEM', @now)";
        cmd.Parameters.AddWithValue("@id", batchId);
        cmd.Parameters.AddWithValue("@rule", rule);
        cmd.Parameters.AddWithValue("@input", inputData);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task Bridge_rule_invokes_registered_mrp_bridge()
    {
        using var client = Client();   // 부팅 확정(스키마)
        SeedBatch("MRP-NIGHTLY", "bridge:pom.mrp.run");
        _factory.Bridge.NextResult = new MrpRunResult("MRP-BATCH-1", "Success", 2, 5, null);

        var res = await client.PostAsync("/api/v1/sys/admin/batch/MRP-NIGHTLY/run", content: null);

        res.StatusCode.Should().Be(HttpStatusCode.OK, "브리지 룰 성공 실행은 200 + 결과 본문");
        _factory.Bridge.LastExecutedBy.Should().Be("batch-bridge-tester", "배치 실행자가 브리지 executedBy로 전달");
        (await res.Content.ReadAsStringAsync()).Should().Contain("\"affected\":5", "affected=계획오더 제안 수");
    }

    [Fact]
    public async Task Bridge_rule_reads_bucket_options_from_input_data()
    {
        using var client = Client();
        SeedBatch("MRP-WEEKLY", "bridge:pom.mrp.run", "{\"bucketDays\":7,\"horizonBuckets\":4}");
        _factory.Bridge.NextResult = new MrpRunResult("MRP-BATCH-B", "Success", 1, 2, null);

        var res = await client.PostAsync("/api/v1/sys/admin/batch/MRP-WEEKLY/run", content: null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        _factory.Bridge.LastRunOptions.Should().Be(new MrpRunOptions(7, 4),
            "버킷 옵션은 배치 정의(BATCH_INPUTDATA)가 소유 — 코드 배포 없이 운영 전환");
    }

    [Fact]
    public async Task Unknown_bridge_key_fails_loudly()
    {
        using var client = Client();
        SeedBatch("BAD-BRIDGE", "bridge:no.such.bridge");

        var res = await client.PostAsync("/api/v1/sys/admin/batch/BAD-BRIDGE/run", content: null);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().Contain("알 수 없는 브리지 룰", "미지 키는 무음 스킵이 아니라 실패 보고");
        body.Should().Contain("\"success\":false");
    }
}
