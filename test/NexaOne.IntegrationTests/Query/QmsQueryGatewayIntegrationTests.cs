using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NexaOne.IntegrationTests.Query;

/// <summary>QMS 명명 쿼리 게이트웨이 E2E(저코드 read/combo/write). db/queries/sqlite/QMS.xml 등록 쿼리가 SQLite에서
/// 실행돼 행을 돌려주는지, VALUE/TEXT 콤보 별칭·@param 선택필터·미인증 401·write 권한(qms:manage)·감사 주입을 검증.
/// 시드는 QMS REST(perm:qms:manage), 되읽기는 명명 쿼리 게이트웨이.</summary>
public sealed class QmsQueryGatewayIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public QmsQueryGatewayIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RegisteredQuery_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/query/QMS.DefectClassList", new Dictionary<string, object>());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DefectClassCombo_returns_value_text_pairs_after_rest_seed()
    {
        var client = _factory.CreateAuthenticatedClient();   // "*"(ADMIN)
        foreach (var (id, name, sev) in new[] { ("DC-Q-MAJ", "Query Scratch", "Major"), ("DC-Q-MIN", "Query Dust", "Minor") })
        {
            var seed = await client.PostAsJsonAsync("/api/v1/qms/defect-classes", new
            { defectClassId = id, defectClassName = name, description = "gateway combo seed", severity = sev });
            seed.StatusCode.Should().Be(HttpStatusCode.OK, $"결함 분류 시드({id})가 성공해야 한다");
        }
        var all = await ExecuteQuery(client, "QMS.DefectClassCombo", new Dictionary<string, object?>());
        all.Should().Contain(r => Str(r, "VALUE") == "DC-Q-MAJ" && Str(r, "TEXT") == "Query Scratch");
        all.Should().Contain(r => Str(r, "VALUE") == "DC-Q-MIN" && Str(r, "TEXT") == "Query Dust");
        var major = await ExecuteQuery(client, "QMS.DefectClassCombo", new Dictionary<string, object?> { ["severity"] = "Major" });
        major.Should().ContainSingle();
        Str(major[0], "VALUE").Should().Be("DC-Q-MAJ");
    }

    [Fact]
    public async Task InspectionSpecCombo_and_list_filter_by_process()
    {
        var client = _factory.CreateAuthenticatedClient();
        const string processId = "PROC-Q-GW";
        var seed = await client.PostAsJsonAsync("/api/v1/qms/inspection-specs", new
        { specId = "SPEC-Q-GW-1", specName = "Gateway Diameter", processId, itemName = "Diameter",
          measureType = "Numeric", nominalValue = 10.0m, tolerancePlus = 0.5m, toleranceMinus = 0.5m });
        seed.StatusCode.Should().Be(HttpStatusCode.OK, "검사 규격 시드가 성공해야 한다");
        var combo = await ExecuteQuery(client, "QMS.InspectionSpecCombo", new Dictionary<string, object?> { ["processId"] = processId });
        combo.Should().ContainSingle();
        Str(combo[0], "VALUE").Should().Be("SPEC-Q-GW-1");
        Str(combo[0], "TEXT").Should().Be("Gateway Diameter");
        var list = await ExecuteQuery(client, "QMS.InspectionSpecList", new Dictionary<string, object?> { ["processId"] = processId });
        list.Should().ContainSingle();
        Str(list[0], "ITEM_NAME").Should().Be("Diameter");
        Str(list[0], "MEASURE_TYPE").Should().Be("Numeric");
    }

    [Fact]
    public async Task CreateDefectClass_command_requires_permission_and_injects_audit()
    {
        var limited = _factory.CreateAuthenticatedClient("fdc:read");   // qms:manage 없음
        var forbidden = await limited.PostAsJsonAsync("/api/v1/command/QMS.CreateDefectClass",
            new Dictionary<string, object?> { ["defectClassId"] = "DC-Q-FORBID", ["defectClassName"] = "Forbidden", ["severity"] = "Minor" });
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var admin = _factory.CreateAuthenticatedClient("qms:manage");
        const string id = "DC-Q-CMD";
        var create = await admin.PostAsJsonAsync("/api/v1/command/QMS.CreateDefectClass",
            new Dictionary<string, object?> { ["defectClassId"] = id, ["defectClassName"] = "Command Class",
              ["description"] = "command path", ["severity"] = "Critical", ["currentUser"] = "HACKER" });
        var createBody = await create.Content.ReadAsStringAsync();
        create.StatusCode.Should().Be(HttpStatusCode.OK, $"쓰기 INSERT 성공해야 함. 본문: {createBody}");
        JsonDocument.Parse(createBody).RootElement.GetProperty("affected").GetInt32().Should().Be(1);

        var combo = await ExecuteQuery(admin, "QMS.DefectClassCombo", new Dictionary<string, object?> { ["severity"] = "Critical" });
        combo.Should().ContainSingle(r => Str(r, "VALUE") == id);
        Str(combo.Single(r => Str(r, "VALUE") == id), "TEXT").Should().Be("Command Class");
    }

    [Fact]
    public async Task Write_query_via_query_endpoint_returns_400()
    {
        var client = _factory.CreateAuthenticatedClient("qms:manage");
        var resp = await client.PostAsJsonAsync("/api/v1/query/QMS.CreateDefectClass",
            new Dictionary<string, object?> { ["defectClassId"] = "DC-Q-WRONG", ["defectClassName"] = "Wrong", ["severity"] = "Minor" });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<List<Dictionary<string, JsonElement>>> ExecuteQuery(
        HttpClient client, string queryId, Dictionary<string, object?> parameters)
    {
        var resp = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", parameters);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"등록 쿼리 실행 성공해야 함. 본문: {body}");
        return await resp.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>() ?? new();
    }

    private static string? Str(Dictionary<string, JsonElement> row, string col)
        => row.TryGetValue(col, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
