using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Dapper;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using NexaDB.Data.Abstractions.Interfaces;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Prc;
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
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<IPurchaseOrderCommandBridge>(sp =>
                {
                    var dataSource = new EesDataSource
                    {
                        Provider = sp.GetRequiredService<IDatabaseProvider>(),
                        ConnectionString = ConnString,
                    };
                    return new NexaOne.PRC.Module(dataSource,
                        new BusinessMasterDirectory(dataSource)).GetPurchaseOrderCommandBridge();
                });
                services.AddSingleton<IPurchaseOrderHoldBridge>(sp =>
                {
                    var dataSource = new EesDataSource
                    {
                        Provider = sp.GetRequiredService<IDatabaseProvider>(),
                        ConnectionString = ConnString,
                    };
                    return new NexaOne.PRC.Module(dataSource,
                        new BusinessMasterDirectory(dataSource)).GetPurchaseOrderHoldBridge();
                });
            });
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
    public async Task Purchase_order_api_transitions_are_state_guarded()
    {
        // PRC 모듈이 상태 가드를 집행하고, 불허 상태는 409로 보고한다.
        var poId = $"PO_{Suffix()}";
        var client = AuthedClient("bulk-prc", "prc:manage");
        (await client.PostAsJsonAsync("/api/v1/prc/purchase-orders", new Dictionary<string, object>
        {
            ["purchaseOrderId"] = poId, ["plantId"] = "PLANT01", ["purchaseOrderName"] = "일괄 전이 검증",
            ["vendorId"] = "V1", ["orderQuantity"] = 10,
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        async Task<string> StatusOf() =>
            (await Query("PRC.PurchaseOrderList", new()))
                .Single(r => r["PURCHASE_ORDER_ID"].ToString() == poId)["STATUS"]!.ToString()!;
        async Task<HttpStatusCode> Close()
        {
            var response = await client.PostAsync($"/api/v1/prc/purchase-orders/{poId}/close", null);
            return response.StatusCode;
        }

        (await StatusOf()).Should().Be("Draft", "생성 기본 상태(DDL DEFAULT)");

        // Draft 상태에서는 마감 불가.
        (await Close()).Should().Be(HttpStatusCode.Conflict);
        (await StatusOf()).Should().Be("Draft", "가드된 전이는 소스 상태 밖 행을 건드리지 않는다");

        SetPurchaseStatus(poId, "Draft", "Incoming");
        (await Close()).Should().Be(HttpStatusCode.Conflict, "품목이 없는 비정상 Incoming 행도 마감할 수 없다");
        SetPurchaseStatus(poId, "Incoming", "Draft");

        // 레거시 확정과 같이 품목이 없는 발주는 Draft를 벗어나지 못한다.
        var emptyOrder = await client.PostAsync($"/api/v1/prc/purchase-orders/{poId}/order", null);
        emptyOrder.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await StatusOf()).Should().Be("Draft");
        SeedPurchaseItem(poId);
        SeedPurchaseItem(poId, "SECOND-PRODUCT", 5);

        // 발주(Draft→Ordered)는 입고 완료 전까지 마감할 수 없다.
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{poId}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusOf()).Should().Be("Ordered");
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{poId}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict, "같은 전이는 다시 적용되지 않는다");

        (await Close()).Should().Be(HttpStatusCode.Conflict, "Ordered만으로 입고 완료를 뜻하지 않는다");
        SetPurchaseStatus(poId, "Ordered", "Incoming");
        SetIncomingQuantity(poId, "TEST-PRODUCT", 5);
        (await Close()).Should().Be(HttpStatusCode.Conflict, "부분 입고는 마감할 수 없다");
        SetIncomingQuantity(poId, "TEST-PRODUCT", 10);
        (await Close()).Should().Be(HttpStatusCode.Conflict, "한 품목이라도 미입고면 마감할 수 없다");
        SetIncomingQuantity(poId, "SECOND-PRODUCT", 5);
        (await Close()).Should().Be(HttpStatusCode.OK);
        (await StatusOf()).Should().Be("Closed");
        (await Close()).Should().Be(HttpStatusCode.Conflict, "이미 마감된 발주는 다시 마감할 수 없다");

        using (var connection = new SqliteConnection(_factory.ConnString))
        {
            connection.Open();
            var actor = connection.ExecuteScalar<string>(
                "SELECT UPDATED_BY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id = poId });
            actor.Should().Be("bulk-prc", "감사 실행자는 요청 본문이 아닌 JWT에서 와야 한다");
        }

        // 무권한 → 403.
        (await AuthedClient("bulk-noperm").PostAsync($"/api/v1/prc/purchase-orders/{poId}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Removed named SQL writes must not remain reachable through the generic gateway.
        (await client.PostAsJsonAsync("/api/v1/command/PRC.OrderPurchaseOrder",
            new { purchaseOrderId = poId })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/v1/command/PRC.ClosePurchaseOrder",
            new { purchaseOrderId = poId })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/v1/command/PRC.CreatePurchaseOrder",
            new { purchaseOrderId = poId })).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.PostAsJsonAsync("/api/v1/command/PRC.DeletePurchaseOrder",
            new { purchaseOrderId = poId })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Purchase_order_cancel_is_terminal_and_excludes_the_scheduled_receipt()
    {
        var client = AuthedClient("prc-canceller", "prc:manage");
        var draftId = $"PO_{Suffix()}";
        var orderedId = $"PO_{Suffix()}";
        var productId = $"CANCEL_PRODUCT_{Suffix()}";
        async Task Save(string id) => (await client.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = id, plantId = "PLANT01", orderQuantity = 10m,
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        async Task<HttpStatusCode> Cancel(string id) =>
            (await client.PostAsync($"/api/v1/prc/purchase-orders/{id}/cancel", null)).StatusCode;

        (await Cancel("NOT_FOUND")).Should().Be(HttpStatusCode.NotFound);
        (await Cancel(new string('x', 51))).Should().Be(HttpStatusCode.BadRequest);
        (await _factory.CreateClient().PostAsync($"/api/v1/prc/purchase-orders/{draftId}/cancel", null))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuthedClient("reader", "prc:read")
            .PostAsync($"/api/v1/prc/purchase-orders/{draftId}/cancel", null))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        await Save(draftId);
        using (var connection = new SqliteConnection(_factory.ConnString))
        {
            connection.Open();
            connection.Execute("UPDATE PRC_PURCHASE_ORDER SET IS_HOLD='Y' WHERE PURCHASE_ORDER_ID=@id",
                new { id = draftId }).Should().Be(1);
        }
        (await Cancel(draftId)).Should().Be(HttpStatusCode.OK,
            "legacy cancellation allows a held Draft order");
        using (var connection = new SqliteConnection(_factory.ConnString))
        {
            connection.Open();
            var row = connection.QuerySingle<(string Status, string IsHold, string UpdatedBy)>(
                "SELECT STATUS AS Status, IS_HOLD AS IsHold, UPDATED_BY AS UpdatedBy " +
                "FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id = draftId });
            row.Should().Be(("Cancelled", "N", "prc-canceller"));
        }
        (await Cancel(draftId)).Should().Be(HttpStatusCode.Conflict);
        (await client.DeleteAsync($"/api/v1/prc/purchase-orders/{draftId}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{draftId}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{draftId}/close", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{draftId}/hold", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{draftId}/release", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = draftId, plantId = "PLANT01", orderQuantity = 20m,
        })).StatusCode.Should().Be(HttpStatusCode.Conflict);

        await Save(orderedId);
        SeedPurchaseItem(orderedId, productId, 10);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{orderedId}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var source = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString,
        };
        var planning = new NexaOne.PRC.Module(source,
            new BusinessMasterDirectory(source)).GetPurchaseOrderPlanningBridge();
        (await planning.GetScheduledReceiptsAsync()).Should()
            .ContainSingle(receipt => receipt.ProductId == productId && receipt.Quantity == 10m);
        (await Cancel(orderedId)).Should().Be(HttpStatusCode.OK);
        (await planning.GetScheduledReceiptsAsync()).Should()
            .NotContain(receipt => receipt.ProductId == productId);

        var receivedId = $"PO_{Suffix()}";
        await Save(receivedId);
        SeedPurchaseItem(receivedId);
        SetIncomingQuantity(receivedId, "TEST-PRODUCT", 1);
        (await Cancel(receivedId)).Should().Be(HttpStatusCode.Conflict,
            "a received line cannot be cancelled even if the header is still Draft");
        SetPurchaseStatus(receivedId, "Draft", "Ordered");
        (await Cancel(receivedId)).Should().Be(HttpStatusCode.Conflict,
            "a received line cannot be cancelled even if the header is still Ordered");
        SetPurchaseStatus(receivedId, "Ordered", "Incoming");
        (await Cancel(receivedId)).Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Purchase_order_draft_edit_and_delete_cannot_change_ordered_or_closed_rows()
    {
        var id = $"PO_{Suffix()}";
        var client = AuthedClient("prc-editor", "prc:manage");
        var draft = new Dictionary<string, object>
        {
            ["purchaseOrderId"] = id, ["plantId"] = "PLANT01",
            ["purchaseOrderName"] = "original", ["vendorId"] = "V1", ["orderQuantity"] = 10,
        };

        async Task<HttpStatusCode> Save() =>
            (await client.PostAsJsonAsync("/api/v1/prc/purchase-orders", draft)).StatusCode;
        async Task<HttpStatusCode> Delete(string purchaseOrderId) =>
            (await client.DeleteAsync($"/api/v1/prc/purchase-orders/{purchaseOrderId}")).StatusCode;

        async Task<(string Name, string Status)> Row()
        {
            var row = (await Query("PRC.PurchaseOrderList", new()))
                .Single(r => r["PURCHASE_ORDER_ID"].ToString() == id);
            return (row["PURCHASE_ORDER_NAME"].ToString()!, row["STATUS"].ToString()!);
        }

        (await Save()).Should().Be(HttpStatusCode.OK);
        draft["purchaseOrderName"] = "edited draft";
        (await Save()).Should().Be(HttpStatusCode.OK);
        SeedPurchaseItem(id);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{id}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        draft["purchaseOrderName"] = "illegal edit";
        (await Save()).Should().Be(HttpStatusCode.Conflict);
        (await Delete(id)).Should().Be(HttpStatusCode.Conflict);
        (await Row()).Should().Be(("edited draft", "Ordered"));

        SetPurchaseStatus(id, "Ordered", "Incoming");
        SetIncomingQuantity(id, "TEST-PRODUCT", 10);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{id}/close", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await Save()).Should().Be(HttpStatusCode.Conflict);
        (await Delete(id)).Should().Be(HttpStatusCode.Conflict);
        (await Row()).Should().Be(("edited draft", "Closed"));

        var deletableId = $"PO_{Suffix()}";
        draft["purchaseOrderId"] = deletableId;
        (await Save()).Should().Be(HttpStatusCode.OK);
        (await Delete(deletableId)).Should().Be(HttpStatusCode.NoContent);
        (await Query("PRC.PurchaseOrderList", new())).Should()
            .NotContain(r => r["PURCHASE_ORDER_ID"].ToString() == deletableId);
    }

    [Fact]
    public async Task Held_purchase_order_cannot_be_edited_deleted_or_advanced()
    {
        var id = $"PO_{Suffix()}";
        var client = AuthedClient("prc-holder", "prc:manage");
        var draft = new Dictionary<string, object>
        {
            ["purchaseOrderId"] = id, ["plantId"] = "PLANT01",
            ["purchaseOrderName"] = "original", ["vendorId"] = "V1", ["orderQuantity"] = 10,
        };

        async Task<HttpStatusCode> Save() =>
            (await client.PostAsJsonAsync("/api/v1/prc/purchase-orders", draft)).StatusCode;
        async Task<HttpStatusCode> Delete() =>
            (await client.DeleteAsync($"/api/v1/prc/purchase-orders/{id}")).StatusCode;

        void SetHold(string flag)
        {
            using var connection = new SqliteConnection(_factory.ConnString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE PRC_PURCHASE_ORDER SET IS_HOLD = @flag WHERE PURCHASE_ORDER_ID = @id";
            command.Parameters.AddWithValue("@flag", flag);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery().Should().Be(1);
        }

        (await Save()).Should().Be(HttpStatusCode.OK);
        SetHold("Y");
        draft["purchaseOrderName"] = "illegal edit";
        (await Save()).Should().Be(HttpStatusCode.Conflict);
        (await Delete()).Should().Be(HttpStatusCode.Conflict);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{id}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        SetHold("N");
        SeedPurchaseItem(id);
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{id}/order", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        SetPurchaseStatus(id, "Ordered", "Incoming");
        SetIncomingQuantity(id, "TEST-PRODUCT", 10);
        SetHold("Y");
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{id}/close", null))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        SetHold("N");
        (await client.PostAsync($"/api/v1/prc/purchase-orders/{id}/close", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Draft_delete_removes_lines_without_fk_and_rejects_received_lines()
    {
        var id = $"PO_{Suffix()}";
        var client = AuthedClient("prc-delete", "prc:manage");
        (await client.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = id, plantId = "PLANT01", orderQuantity = 0m,
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        SeedPurchaseItem(id);
        SeedPurchaseItem(id, "SECOND-PRODUCT", 5);

        SetIncomingQuantity(id, "TEST-PRODUCT", 1);
        (await client.DeleteAsync($"/api/v1/prc/purchase-orders/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        SetIncomingQuantity(id, "TEST-PRODUCT", 0);
        (await client.DeleteAsync($"/api/v1/prc/purchase-orders/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(0);
        connection.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id", new { id })
            .Should().Be(0, "SQLite stores with disabled FK cascades must not retain orphan lines");
    }

    [Fact]
    public async Task Draft_write_requires_manage_permission_and_valid_inputs()
    {
        var id = $"PO_{Suffix()}";
        var body = new { purchaseOrderId = id, plantId = "PLANT01", orderQuantity = 1m };
        (await _factory.CreateClient().PostAsJsonAsync("/api/v1/prc/purchase-orders", body))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await AuthedClient("reader", "prc:read").PostAsJsonAsync(
            "/api/v1/prc/purchase-orders", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var manager = AuthedClient("buyer", "prc:manage");
        (await manager.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = id, plantId = "PLANT01", orderQuantity = -1m,
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await manager.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = id, plantId = "PLANT01", orderQuantity = 1.00001m,
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await manager.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = id, plantId = " ", orderQuantity = 1m,
        })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await manager.DeleteAsync("/api/v1/prc/purchase-orders/NOT_FOUND"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await manager.DeleteAsync($"/api/v1/prc/purchase-orders/{new string('x', 51)}"))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await manager.PostAsJsonAsync("/api/v1/prc/purchase-orders", new
        {
            purchaseOrderId = id, plantId = "PLANT01", orderQuantity = 1m, actorId = "spoofed",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        using (var connection = new SqliteConnection(_factory.ConnString))
        {
            connection.Open();
            connection.ExecuteScalar<string>(
                "SELECT CREATED_BY FROM PRC_PURCHASE_ORDER WHERE PURCHASE_ORDER_ID=@id", new { id })
                .Should().Be("buyer", "draft writes take the actor from JWT, not the request body");
        }
        (await manager.DeleteAsync($"/api/v1/prc/purchase-orders/{id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Query("PRC.PurchaseOrderList", new())).Should()
            .NotContain(row => row["PURCHASE_ORDER_ID"].ToString() == id);
    }

    private void SeedPurchaseItem(string purchaseOrderId, string productId = "TEST-PRODUCT", decimal orderQuantity = 10)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO PRC_PURCHASE_ITEM (PURCHASE_ORDER_ID, PRODUCT_ID, ORDER_QTY)
            VALUES (@id, @productId, @orderQty)
            """;
        command.Parameters.AddWithValue("@id", purchaseOrderId);
        command.Parameters.AddWithValue("@productId", productId);
        command.Parameters.AddWithValue("@orderQty", orderQuantity);
        command.ExecuteNonQuery().Should().Be(1);
        command.CommandText = """
            UPDATE PRC_PURCHASE_ORDER
            SET ORDER_QTY=(SELECT SUM(ORDER_QTY) FROM PRC_PURCHASE_ITEM WHERE PURCHASE_ORDER_ID=@id)
            WHERE PURCHASE_ORDER_ID=@id
            """;
        command.ExecuteNonQuery().Should().Be(1);
        transaction.Commit();
    }

    private void SetPurchaseStatus(string purchaseOrderId, string expectedStatus, string newStatus)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PRC_PURCHASE_ORDER SET STATUS=@newStatus WHERE PURCHASE_ORDER_ID=@id AND STATUS=@expectedStatus";
        command.Parameters.AddWithValue("@id", purchaseOrderId);
        command.Parameters.AddWithValue("@newStatus", newStatus);
        command.Parameters.AddWithValue("@expectedStatus", expectedStatus);
        command.ExecuteNonQuery().Should().Be(1);
    }

    private void SetIncomingQuantity(string purchaseOrderId, string productId, decimal quantity)
    {
        using var connection = new SqliteConnection(_factory.ConnString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE PRC_PURCHASE_ITEM SET INCOMING_QTY=@quantity
            WHERE PURCHASE_ORDER_ID=@id AND PRODUCT_ID=@productId
            """;
        command.Parameters.AddWithValue("@id", purchaseOrderId);
        command.Parameters.AddWithValue("@productId", productId);
        command.Parameters.AddWithValue("@quantity", quantity);
        command.ExecuteNonQuery().Should().Be(1);
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
