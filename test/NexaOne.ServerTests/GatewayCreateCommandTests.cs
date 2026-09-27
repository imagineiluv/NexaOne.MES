using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.Server.Gateway;
using NexaOne.SYS.Infrastructure;
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
    public async Task CreateShift_roundtrip_with_permissions()
    {
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
    public async Task SaveIdRule_allows_metadata_updates_but_never_rewrites_the_issuance_format()
    {
        var ruleId = $"R_{Suffix()}";
        var body = new Dictionary<string, object>
        {
            ["ruleId"] = ruleId, ["ruleName"] = "월별 요청", ["prefix"] = "REQ-{period}-",
            ["seqLength"] = 3, ["resetCycle"] = "MONTHLY", ["description"] = "초기 설명",
            ["currentUser"] = "spoofed",
        };
        var path = "/api/v1/command/COM.SaveIdRule";

        (await AuthedClient("no-com").PostAsJsonAsync(path, body))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var manager = AuthedClient("id-admin", "com:manage");
        var created = await manager.PostAsJsonAsync(path, body);
        created.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Query("COM.IdRuleList", new())).Should().Contain(r => r["RULE_ID"].ToString() == ruleId,
            "유효한 규칙은 저장 명령으로 등록돼야 한다");
        (await created.Content.ReadFromJsonAsync<AffectedRowsResponse>())!.Affected.Should().Be(1);

        var engine = new IdRuleEngine(new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString,
        }, () => new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc));
        (await engine.NextIdAsync(ruleId)).Should().Be("REQ-202609-001");

        body["ruleName"] = "월별 요청 수정";
        body["description"] = "수정 설명";
        body["seqLength"] = "3"; // 메타 폼은 정수 필드도 문자열로 전달한다.
        var updated = await manager.PostAsJsonAsync(path, body);
        (await updated.Content.ReadFromJsonAsync<AffectedRowsResponse>())!.Affected.Should().Be(1);

        body["prefix"] = "CHANGED-{period}-";
        var formatChange = await manager.PostAsJsonAsync(path, body);
        (await formatChange.Content.ReadFromJsonAsync<AffectedRowsResponse>())!.Affected.Should().Be(0);

        var row = (await Query("COM.IdRuleList", new()))
            .Single(r => r["RULE_ID"].ToString() == ruleId);
        row["RULE_NAME"].ToString().Should().Be("월별 요청 수정");
        row["PREFIX"].ToString().Should().Be("REQ-{period}-");
        row["CURRENT_SEQ"].ToString().Should().Be("1");

        using var connection = new SqliteConnection(_factory.ConnString);
        await connection.OpenAsync();
        using var audit = connection.CreateCommand();
        audit.CommandText = "SELECT CREATED_BY, UPDATED_BY, SEQ_PERIOD FROM COM_ID_RULE WHERE RULE_ID=@id";
        audit.Parameters.AddWithValue("@id", ruleId);
        using var reader = await audit.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetString(0).Should().Be("id-admin");
        reader.GetString(1).Should().Be("id-admin");
        reader.GetString(2).Should().Be("202609");
    }

    [Fact]
    public async Task SaveIdRule_rejects_invalid_cycle_format_and_length_without_creating_rows()
    {
        var ruleId = $"R_{Suffix()}";
        var body = new Dictionary<string, object>
        {
            ["ruleId"] = ruleId, ["ruleName"] = "잘못된 규칙", ["prefix"] = "REQ-",
            ["seqLength"] = 3, ["resetCycle"] = "MONTHLY",
        };
        var manager = AuthedClient("id-admin", "com:manage");
        var path = "/api/v1/command/COM.SaveIdRule";

        var noPeriod = await manager.PostAsJsonAsync(path, body);
        (await noPeriod.Content.ReadFromJsonAsync<AffectedRowsResponse>())!.Affected.Should().Be(0);
        body["prefix"] = "REQ-{period}-";
        body["seqLength"] = 0;
        var noLength = await manager.PostAsJsonAsync(path, body);
        (await noLength.Content.ReadFromJsonAsync<AffectedRowsResponse>())!.Affected.Should().Be(0);
        body["seqLength"] = "3.5";
        var fractionalLength = await manager.PostAsJsonAsync(path, body);
        (await fractionalLength.Content.ReadFromJsonAsync<AffectedRowsResponse>())!.Affected.Should().Be(0);
        (await Query("COM.IdRuleList", new())).Should().NotContain(r => r["RULE_ID"].ToString() == ruleId);
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

    [Fact]
    public async Task Bulk_transition_commands_are_state_guarded()
    {
        // 그리드 일괄 명령 — 가드된 전이: 허용 소스 상태만 UPDATE, 그 외는 무영향(affected 0이어도 200).
        var poId = $"PO_{Suffix()}";
        var client = AuthedClient("bulk-prc", "prc:manage");
        (await client.PostAsJsonAsync("/api/v1/command/PRC.CreatePurchaseOrder", new Dictionary<string, object>
        {
            ["purchaseOrderId"] = poId, ["plantId"] = "PLANT01", ["purchaseOrderName"] = "일괄 전이 검증",
            ["vendorId"] = "V1", ["orderQty"] = 10,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        async Task<string> StatusOf() =>
            (await Query("PRC.PurchaseOrderList", new()))
                .Single(r => r["PURCHASE_ORDER_ID"].ToString() == poId)["STATUS"]!.ToString()!;

        (await StatusOf()).Should().Be("Draft", "생성 기본 상태(DDL DEFAULT)");

        // Draft 상태에서 '마감' 시도 → 가드 미충족(Ordered/Incoming 아님) — 200이지만 상태 불변.
        (await client.PostAsJsonAsync("/api/v1/command/PRC.ClosePurchaseOrder",
            new Dictionary<string, object> { ["purchaseOrderId"] = poId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf()).Should().Be("Draft", "가드된 전이는 소스 상태 밖 행을 건드리지 않는다");

        // 발주(Draft→Ordered) → 마감(Ordered→Closed) 순차 전이.
        (await client.PostAsJsonAsync("/api/v1/command/PRC.OrderPurchaseOrder",
            new Dictionary<string, object> { ["purchaseOrderId"] = poId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf()).Should().Be("Ordered");

        (await client.PostAsJsonAsync("/api/v1/command/PRC.ClosePurchaseOrder",
            new Dictionary<string, object> { ["purchaseOrderId"] = poId }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf()).Should().Be("Closed");

        // 무권한 → 403.
        (await AuthedClient("bulk-noperm").PostAsJsonAsync("/api/v1/command/PRC.OrderPurchaseOrder",
            new Dictionary<string, object> { ["purchaseOrderId"] = poId }))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private async Task<List<Dictionary<string, object>>> Query(string queryId, Dictionary<string, object> p)
    {
        var module = queryId.Split('.')[0].ToLowerInvariant();
        var permission = queryId == "COM.MailServerList" ? "com:manage" : $"{module}:read";
        var res = await AuthedClient("cmd-reader", permission).PostAsJsonAsync($"/api/v1/query/{queryId}", p);
        res.StatusCode.Should().Be(HttpStatusCode.OK, $"{queryId} 는 200이어야 한다");
        var rows = await res.Content.ReadFromJsonAsync<List<Dictionary<string, object>>>();
        rows.Should().NotBeNull();
        return rows!;
    }
}
