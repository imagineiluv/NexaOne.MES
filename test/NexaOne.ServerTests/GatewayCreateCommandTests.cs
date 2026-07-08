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

/// <summary>마스터 등록 폼(SaveQueryId) 쓰기 게이트웨이 E2E — modules OFF + SQLite. 신설 kind="write" 쿼리
/// (MDM.CreateVendor/CreateShift 등)를 /api/v1/command/{id}로 실행해 (1)요구권한 보유 시 INSERT+감사 주입
/// (@currentUser) (2)무권한 403 (3)등록 행이 read 쿼리로 라운드트립되는지 검증한다. 나머지 Create 쿼리의
/// 방언/메타(kind·requiredPermission) 정합은 DialectParityTests가 자동 가드.</summary>
public sealed class GatewayCreateCommandTests : IClassFixture<GatewayCreateCommandTests.CmdFactory>
{
    private const string Secret = "create-cmd-gateway-e2e-jwt-secret-32bytes+!!";
    private const string Issuer = "nexaone-createcmd-test";
    private readonly CmdFactory _factory;
    public GatewayCreateCommandTests(CmdFactory factory) => _factory = factory;

    public sealed class CmdFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-createcmd-{Guid.NewGuid():N}.db");
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
    public async Task CreateVendor_requires_permission_and_roundtrips()
    {
        var vendorId = $"VEN_{Suffix()}";
        var body = new Dictionary<string, object>
        {
            ["vendorId"] = vendorId, ["vendorName"] = "신규 공급사", ["vendorType"] = "Material",
            ["phone"] = "02-111-2222", ["email"] = "new@x.com",
        };

        // 무권한 → 403 (requiredPermission="mdm:manage" 집행).
        var forbidden = await AuthedClient("cmd-noperm").PostAsJsonAsync("/api/v1/command/MDM.CreateVendor", body);
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden, "쓰기 쿼리는 요구권한 없는 사용자를 거부해야 한다");

        // mdm:manage → 200 + INSERT.
        var creator = $"creator_{Suffix()}";
        var ok = await AuthedClient(creator, "mdm:manage").PostAsJsonAsync("/api/v1/command/MDM.CreateVendor", body);
        ok.StatusCode.Should().Be(HttpStatusCode.OK, "mdm:manage 보유자는 등록 성공");

        var rows = await Query("MDM.VendorList", new());
        var created = rows.SingleOrDefault(r => r["VENDOR_ID"].ToString() == vendorId);
        created.Should().NotBeNull("등록 폼으로 INSERT된 벤더가 read 쿼리로 라운드트립돼야 한다");
        created!["VENDOR_NAME"].ToString().Should().Be("신규 공급사");
    }

    [Fact]
    public async Task CreateWorkOrder_and_CreateShift_roundtrip_with_permissions()
    {
        var wo = $"WO_{Suffix()}";
        var plant = $"P_{Suffix()}";
        var okWo = await AuthedClient("cmd-pom", "pom:manage").PostAsJsonAsync("/api/v1/command/POM.CreateWorkOrder",
            new Dictionary<string, object>
            {
                ["workOrderId"] = wo, ["plantId"] = plant, ["workOrderName"] = "신규 작업",
                ["equipmentId"] = "EQX", ["productId"] = "ITEMX", ["planQty"] = 250,
            });
        okWo.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Query("POM.WorkOrderList", new() { ["plantId"] = plant }))
            .Select(r => r["WORK_ORDER_ID"].ToString()).Should().Contain(wo, "W/O 등록 폼 라운드트립");

        var shift = $"SH_{Suffix()}";
        var okShift = await AuthedClient("cmd-mdm", "mdm:manage").PostAsJsonAsync("/api/v1/command/MDM.CreateShift",
            new Dictionary<string, object>
            { ["shiftId"] = shift, ["shiftName"] = "특근조", ["startTime"] = "06:00", ["endTime"] = "14:00" });
        okShift.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Query("MDM.ShiftList", new()))
            .Select(r => r["SHIFT_ID"].ToString()).Should().Contain(shift, "작업조 등록 폼 라운드트립");
    }

    [Fact]
    public async Task CreateIndex_and_CreateMailServer_roundtrip_with_permissions()
    {
        // EST.CreateIndex — est:manage 집행(무권한 403 → 권한 200 + read 라운드트립).
        var idx = $"IDX_{Suffix()}";
        var idxBody = new Dictionary<string, object>
        { ["indexId"] = idx, ["indexName"] = "신규 지표", ["indexCategory"] = "가동", ["unit"] = "%" };
        (await AuthedClient("cmd-noperm").PostAsJsonAsync("/api/v1/command/EST.CreateIndex", idxBody))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AuthedClient("cmd-est", "est:manage").PostAsJsonAsync("/api/v1/command/EST.CreateIndex", idxBody))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Query("EST.IndexList", new())).Select(r => r["INDEX_ID"].ToString()).Should().Contain(idx);

        // COM.CreateMailServer — com:manage.
        var srv = $"SRV_{Suffix()}";
        var ok = await AuthedClient("cmd-com", "com:manage").PostAsJsonAsync("/api/v1/command/COM.CreateMailServer",
            new Dictionary<string, object>
            { ["serverId"] = srv, ["serverName"] = "보조 SMTP", ["host"] = "smtp2.x.com", ["port"] = 25, ["senderAddress"] = "no@x.com", ["useSsl"] = "N" });
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Query("COM.MailServerList", new())).Select(r => r["SERVER_ID"].ToString()).Should().Contain(srv);
    }

    [Fact]
    public async Task Create_command_via_query_route_is_rejected()
    {
        var res = await AuthedClient("cmd-any", "mdm:manage").PostAsJsonAsync("/api/v1/query/MDM.CreateVendor",
            new Dictionary<string, object> { ["vendorId"] = "X" });
        res.StatusCode.Should().Be(HttpStatusCode.BadRequest, "쓰기 쿼리는 /query 라우트로 실행될 수 없다(WRITE_QUERY_VIA_QUERY)");
    }

    [Fact]
    public async Task Create_command_upserts_on_same_pk_without_duplicate()
    {
        // 그리드 표준 편집(upsert 전환) — 행선택→폼수정→저장이 PK 충돌 대신 UPDATE가 되는지.
        var vendorId = $"VEN_{Suffix()}";
        var first = await AuthedClient("upsert-a", "mdm:manage").PostAsJsonAsync("/api/v1/command/MDM.CreateVendor",
            new Dictionary<string, object>
            {
                ["vendorId"] = vendorId, ["vendorName"] = "최초 등록명", ["vendorType"] = "Material",
                ["phone"] = "02-111-1111", ["email"] = "v1@x.com",
            });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // 같은 PK로 이름/연락처만 바꿔 재저장 → 200(UPDATE, PK 충돌 아님).
        var second = await AuthedClient("upsert-b", "mdm:manage").PostAsJsonAsync("/api/v1/command/MDM.CreateVendor",
            new Dictionary<string, object>
            {
                ["vendorId"] = vendorId, ["vendorName"] = "수정된 공급사명", ["vendorType"] = "Material",
                ["phone"] = "02-222-2222", ["email"] = "v2@x.com",
            });
        second.StatusCode.Should().Be(HttpStatusCode.OK, "동일 PK 재저장은 upsert(UPDATE)여야 한다");

        var rows = (await Query("MDM.VendorList", new())).Where(r => r["VENDOR_ID"].ToString() == vendorId).ToList();
        rows.Should().HaveCount(1, "upsert는 중복 행을 만들지 않는다");
        rows[0]["VENDOR_NAME"].ToString().Should().Be("수정된 공급사명");
        rows[0]["PHONE"].ToString().Should().Be("02-222-2222");
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> p)
    {
        var res = await AuthedClient("cmd-reader").PostAsJsonAsync($"/api/v1/query/{queryId}", p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
