using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NexaOne.IntegrationTests.Query;

/// <summary>
/// 파일 기반 명명 쓰기쿼리 게이트웨이(POST /api/v1/command/{queryId}) end-to-end 검증.
/// db/queries/sqlite/MDM.xml 의 쓰기 쿼리(MDM.CreatePlant, kind="write")가 SQLite에서 실제로 INSERT 하고,
/// 감사 컬럼(CREATED_BY/UPDATED_BY)을 게이트웨이가 토큰(@currentUser)으로 주입하며(클라이언트 위변조 무시),
/// 인증·데이터주도 인가(requiredPermission)·읽기/쓰기 종류 가드가 올바른지 확인한다(저코드 쓰기 경로).
/// </summary>
public sealed class CommandGatewayIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public CommandGatewayIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RegisteredCommand_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant",
            new Dictionary<string, object?> { ["plantId"] = "P-C-NOAUTH", ["plantName"] = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "쓰기 게이트웨이도 인증을 요구해야 한다");
    }

    [Fact]
    public async Task UnknownCommandId_returns_404()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/v1/command/NOPE.DoesNotExist",
            new Dictionary<string, object?>());
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "미등록 쓰기 쿼리 ID는 404여야 한다");
    }

    [Fact]
    public async Task CommandWithRequiredPermission_forbidden_without_permission()
    {
        // MDM.CreatePlant은 requiredPermission="mdm:manage" — 권한 없는 토큰은 403, 쓰기 미수행.
        var limited = _factory.CreateAuthenticatedClient("fdc:read");   // mdm:manage 없음
        var resp = await limited.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant",
            new Dictionary<string, object?> { ["plantId"] = "P-C-FORBID", ["plantName"] = "Forbidden" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "필요 권한(mdm:manage)이 없으면 쓰기 쿼리 실행은 403이어야 한다");
    }

    [Fact]
    public async Task Read_query_via_command_endpoint_returns_400()
    {
        // 읽기 쿼리(MDM.PlantList, kind 미선언=read)를 command 게이트웨이로 실행하면 400(종류 가드).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/v1/command/MDM.PlantList",
            new Dictionary<string, object?>());
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "읽기 쿼리를 command로 실행하면 종류 가드로 400이어야 한다");
    }

    [Fact]
    public async Task Write_query_via_query_endpoint_returns_400()
    {
        // 쓰기 쿼리(MDM.CreatePlant)를 읽기 게이트웨이(/query)로 실행하면 400(종류 가드 — 우발적 SELECT-as-write 방지).
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/v1/query/MDM.CreatePlant",
            new Dictionary<string, object?> { ["plantId"] = "P-C-WRONG", ["plantName"] = "Wrong" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "쓰기 쿼리를 /query로 실행하면 종류 가드로 400이어야 한다");
    }

    [Fact]
    public async Task Command_inserts_row_and_gateway_injects_audit_overriding_client_forgery()
    {
        var client = _factory.CreateAuthenticatedClient();   // ADMIN "*", 토큰 sub = "test-admin"
        const string plantId = "P-CMD-AUDIT";

        // 본문에 감사 컬럼 위변조 시도(currentUser="HACKER")를 끼워 보낸다 — 게이트웨이가 토큰값으로 덮어써야 한다.
        var create = await client.PostAsJsonAsync("/api/v1/command/MDM.CreatePlant", new Dictionary<string, object?>
        {
            ["plantId"]    = plantId,
            ["plantName"]  = "Command Plant",
            ["country"]    = "KR",
            ["timeZone"]   = "Asia/Seoul",
            ["currentUser"] = "HACKER",   // 위변조 시도 — 무시되어야 한다.
        });
        var createBody = await create.Content.ReadAsStringAsync();
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"쓰기 쿼리 INSERT가 성공해야 한다. 응답 본문: {createBody}");

        var affected = JsonDocument.Parse(createBody).RootElement.GetProperty("affected").GetInt32();
        affected.Should().Be(1, "INSERT는 1행에 영향을 줘야 한다");

        // 읽기 게이트웨이로 영속 확인 — 비즈니스 컬럼 왕복.
        var rows = await ReadQuery(client, "MDM.PlantList", new() { ["plantId"] = plantId });
        rows.Should().ContainSingle();
        Str(rows[0], "PLANT_NAME").Should().Be("Command Plant");
        Str(rows[0], "COUNTRY").Should().Be("KR");

        // 감사 컬럼 — 게이트웨이가 토큰 sub("test-admin")로 주입했고, 클라이언트의 "HACKER"는 무시됐어야 한다.
        var audit = await ReadQuery(client, "MDM.PlantAudit", new() { ["plantId"] = plantId });
        audit.Should().ContainSingle();
        Str(audit[0], "CREATED_BY").Should().Be("test-admin", "감사 작성자는 토큰에서 주입돼야 한다(본문 위변조 무시)");
        Str(audit[0], "CREATED_BY").Should().NotBe("HACKER", "클라이언트가 보낸 currentUser는 무시돼야 한다");
        Str(audit[0], "UPDATED_BY").Should().Be("test-admin");
    }

    private static async Task<List<Dictionary<string, JsonElement>>> ReadQuery(
        HttpClient client, string queryId, Dictionary<string, object?> parameters)
    {
        var resp = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", parameters);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"확인용 읽기 쿼리가 성공해야 한다. 응답 본문: {body}");
        return await resp.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>()
            ?? new List<Dictionary<string, JsonElement>>();
    }

    private static string? Str(Dictionary<string, JsonElement> row, string col)
        => row.TryGetValue(col, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
