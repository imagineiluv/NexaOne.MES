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

/// <summary>그리드 표준 삭제(DeleteQueryId) 쓰기 게이트웨이 E2E — modules OFF + SQLite. 신설 kind="write"
/// Delete 쿼리를 /api/v1/command/{id}로 실행해 (1)무권한 403 (2)권한 보유 시 DELETE (3)메타 그리드가 보내는
/// 듀얼키 페이로드(행 원본 UPPER_SNAKE + camelCase 사본, 여분 파라미터 다수)가 안전하게 매칭되는지 검증한다.
/// 나머지 Delete 쿼리의 방언/메타 정합은 DialectParityTests가 자동 가드.</summary>
public sealed class GatewayDeleteCommandTests : IClassFixture<GatewayDeleteCommandTests.DelFactory>
{
    private const string Secret = "delete-cmd-gateway-e2e-jwt-secret-32bytes!!";
    private const string Issuer = "nexaone-deletecmd-test";
    private readonly DelFactory _factory;
    public GatewayDeleteCommandTests(DelFactory factory) => _factory = factory;

    public sealed class DelFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-deletecmd-{Guid.NewGuid():N}.db");
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

    private HttpClient AuthedClient(string userId, params string[] permissions)
    {
        var client = _factory.CreateClient();
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        claims.AddRange(permissions.Select(p => new Claim(NexaOne.Common.Security.Permissions.ClaimType, p)));
        var token = new JwtSecurityToken(Issuer, Issuer, claims, expires: DateTime.UtcNow.AddMinutes(10), signingCredentials: creds);
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task DeleteVendor_requires_permission_and_removes_row_with_dual_key_payload()
    {
        // 준비 — 벤더 1건 등록(폼 경로와 동일).
        var vendorId = $"VEN_{Suffix()}";
        var create = await AuthedClient("del-setup", "mdm:manage").PostAsJsonAsync("/api/v1/command/MDM.CreateVendor",
            new Dictionary<string, object>
            {
                ["vendorId"] = vendorId, ["vendorName"] = "삭제 대상", ["vendorType"] = "Material",
                ["phone"] = "02-000-0000", ["email"] = "del@x.com",
            });
        create.StatusCode.Should().Be(HttpStatusCode.OK);

        // 메타 그리드 삭제 페이로드 재현 — 행 원본(UPPER_SNAKE 전 컬럼) + camelCase 사본(여분 파라미터 다수).
        var gridPayload = new Dictionary<string, object?>
        {
            ["VENDOR_ID"] = vendorId, ["VENDOR_NAME"] = "삭제 대상", ["VENDOR_TYPE"] = "Material",
            ["PHONE"] = "02-000-0000", ["EMAIL"] = "del@x.com",
            ["vendorId"] = vendorId, ["vendorName"] = "삭제 대상", ["vendorType"] = "Material",
            ["phone"] = "02-000-0000", ["email"] = "del@x.com",
        };

        // 무권한 → 403 (requiredPermission="mdm:manage" 집행).
        var forbidden = await AuthedClient("del-noperm").PostAsJsonAsync("/api/v1/command/MDM.DeleteVendor", gridPayload);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "삭제 쿼리는 요구권한 없는 사용자를 거부해야 한다");
        (await Query("MDM.VendorList", new())).Any(r => r["VENDOR_ID"].ToString() == vendorId)
            .Should().BeTrue("403이면 행이 남아 있어야 한다");

        // mdm:manage → 200 + DELETE (여분 파라미터가 있어도 @vendorId만 매칭).
        var ok = await AuthedClient("del-mgr", "mdm:manage").PostAsJsonAsync("/api/v1/command/MDM.DeleteVendor", gridPayload);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "mdm:manage 보유자는 삭제 성공");
        (await Query("MDM.VendorList", new())).Any(r => r["VENDOR_ID"].ToString() == vendorId)
            .Should().BeFalse("삭제 후 read 쿼리에서 사라져야 한다");
    }

    [Fact]
    public async Task Delete_command_via_query_route_is_rejected()
    {
        var res = await AuthedClient("del-any", "mdm:manage").PostAsJsonAsync("/api/v1/query/MDM.DeleteVendor",
            new Dictionary<string, object> { ["vendorId"] = "X" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "쓰기 쿼리는 /query 라우트로 실행될 수 없다(WRITE_QUERY_VIA_QUERY)");
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> p)
    {
        var res = await AuthedClient("del-reader").PostAsJsonAsync($"/api/v1/query/{queryId}", p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
