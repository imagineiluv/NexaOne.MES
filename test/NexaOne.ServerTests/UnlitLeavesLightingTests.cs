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

/// <summary>미점등 6잎 점등분 E2E — 배치 작업 정의(V066, SYS.*BatchProcess)와 가상 이벤트 정의(V067,
/// FDC.*VirtualEvent)의 게이트웨이 CRUD 왕복 + 권한 게이트를 실 SQLite(전 마이그레이션 적용)로 검증한다.
/// 화면(메타 정의)은 동일 쿼리 ID/@param을 바인딩하므로 이 왕복이 화면 데이터 경로의 계약 검증을 겸한다.
/// 실행/평가 엔진은 의도적 후속(마이그레이션 주석 참조) — 여기서는 정의 관리 범위만 검증한다.</summary>
public sealed class UnlitLeavesLightingTests : IClassFixture<UnlitLeavesLightingTests.HostFactory>
{
    private const string Secret = "unlit-leaves-e2e-jwt-secret-key-at-least-32-bytes!";
    private const string Issuer = "nexaone-unlit-test";
    private readonly HostFactory _factory;
    public UnlitLeavesLightingTests(HostFactory factory) => _factory = factory;

    public sealed class HostFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-unlit-{Guid.NewGuid():N}.db");
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
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private HttpClient Client(params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "unlit-tester") };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static async Task<List<Dictionary<string, object?>>> QueryAsync(HttpClient client, string queryId, object? p = null)
    {
        var res = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", p ?? new Dictionary<string, object>());
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 조회는 200이어야 한다");
        return (await res.Content.ReadFromJsonAsync<List<Dictionary<string, object?>>>())!;
    }

    private static string Col(Dictionary<string, object?> row, string key)
        => row.TryGetValue(key, out var v) && v is not null ? v.ToString() ?? "" : "";

    [Fact]
    public async Task Batch_process_definition_crud_roundtrip_with_permission_gate()
    {
        // 권한 게이트 — sys:manage 없는 쓰기는 403(레지스트리 requiredPermission 집행).
        var noPerm = await Client("fdc:read").PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess",
            new Dictionary<string, object> { ["batchId"] = "B-DENY", ["batchName"] = "거부" });
        noPerm.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = Client("sys:manage");
        // 생성 → 목록 반영(V066 테이블이 실제로 존재해야 통과).
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess", new Dictionary<string, object>
        {
            ["batchId"] = "B-001", ["batchName"] = "야간 집계", ["batchType"] = "Schedule",
            ["batchRule"] = "OeeAggregateDay", ["batchOptions"] = "0 0 2 * * ?", ["batchInputData"] = "{}",
            ["description"] = "E2E",
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await QueryAsync(admin, "SYS.BatchProcessList");
        rows.Count(r => Col(r, "BATCH_ID") == "B-001").Should().Be(1, "생성 후 목록 반영(V066 실존)");

        // 업서트(갱신) — 동일 ID 재저장은 갱신이어야 한다(중복 행 금지).
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.UpsertBatchProcess", new Dictionary<string, object>
        {
            ["batchId"] = "B-001", ["batchName"] = "야간 집계(수정)",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        rows = await QueryAsync(admin, "SYS.BatchProcessList");
        var updated = rows.Where(r => Col(r, "BATCH_ID") == "B-001").ToList();
        updated.Should().HaveCount(1, "업서트는 갱신(중복 행 금지)");
        Col(updated[0], "BATCH_NAME").Should().Be("야간 집계(수정)");

        // 소프트 삭제 → 목록 제외(VALID_STATE 게이트).
        (await admin.PostAsJsonAsync("/api/v1/command/SYS.DeleteBatchProcess",
            new Dictionary<string, object> { ["batchId"] = "B-001" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await QueryAsync(admin, "SYS.BatchProcessList"))
            .Count(r => Col(r, "BATCH_ID") == "B-001").Should().Be(0, "소프트 삭제 후 목록 제외");
    }

    [Fact]
    public async Task Virtual_event_definition_crud_roundtrip_with_permission_gate()
    {
        var noPerm = await Client("sys:manage").PostAsJsonAsync("/api/v1/command/FDC.UpsertVirtualEvent",
            new Dictionary<string, object> { ["equipmentId"] = "EQ1", ["eventId"] = "VE-DENY" });
        noPerm.StatusCode.Should().Be(HttpStatusCode.Forbidden, "가상 이벤트 쓰기는 fdc:manage 전용");

        var fdc = Client("fdc:manage");
        (await fdc.PostAsJsonAsync("/api/v1/command/FDC.UpsertVirtualEvent", new Dictionary<string, object>
        {
            ["plantId"] = "P1", ["equipmentId"] = "EQ1", ["eventId"] = "VE-001",
            ["eventName"] = "과열 감지", ["eventOn"] = "1", ["eventOff"] = "0",
            ["conditionFormula"] = "TEMP > 80 AND PRESSURE > 3", ["description"] = "E2E",
        })).StatusCode.Should().Be(HttpStatusCode.OK, "V067 테이블+레지스트리 실왕복");

        var rows = await QueryAsync(fdc, "FDC.VirtualEventList", new Dictionary<string, object> { ["equipmentId"] = "EQ1" });
        rows.Count(r => Col(r, "EVENT_ID") == "VE-001").Should().Be(1, "생성 후 목록 반영(V067 실존)");
        Col(rows[0], "CONDITION_FORMULA").Should().Contain("TEMP > 80", "수식은 불투명 문자열로 보존(평가 엔진 후속)");

        (await fdc.PostAsJsonAsync("/api/v1/command/FDC.DeleteVirtualEvent",
            new Dictionary<string, object> { ["equipmentId"] = "EQ1", ["eventId"] = "VE-001" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await QueryAsync(fdc, "FDC.VirtualEventList", new Dictionary<string, object> { ["equipmentId"] = "EQ1" }))
            .Should().BeEmpty("소프트 삭제 후 목록 제외");
    }
}
