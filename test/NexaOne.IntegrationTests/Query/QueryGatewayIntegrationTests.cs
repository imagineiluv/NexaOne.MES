using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace NexaOne.IntegrationTests.Query;

/// <summary>
/// 파일 기반 쿼리 레지스트리 게이트웨이(POST /api/v1/query/{queryId}) end-to-end 검증.
/// db/queries/sqlite/MDM.xml 의 등록 쿼리(MDM.PlantList)가 SQLite에서 실제로 실행되어 행을 돌려주는지,
/// 파라미터 바인딩·미등록 ID 404·미인증 401이 올바른지 확인한다(저코드 경로).
/// </summary>
public sealed class QueryGatewayIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public QueryGatewayIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task RegisteredQuery_requires_auth()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/query/MDM.PlantList", new Dictionary<string, object>());
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "쿼리 게이트웨이도 인증을 요구해야 한다");
    }

    [Fact]
    public async Task UnknownQueryId_returns_404()
    {
        var client = _factory.CreateAuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/api/v1/query/NOPE.DoesNotExist", new Dictionary<string, object>());
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound, "미등록 쿼리 ID는 404여야 한다");
    }

    [Fact]
    public async Task RegisteredQuery_executes_and_returns_rows_with_param_filter()
    {
        var client = _factory.CreateAuthenticatedClient();   // ADMIN "*"

        // 데이터 시드 — MDM_PLANT 2건(MDM 쓰기 API 경유).
        foreach (var (id, name) in new[] { ("P-Q-1", "Query Plant 1"), ("P-Q-2", "Query Plant 2") })
        {
            var seed = await client.PostAsJsonAsync("/api/v1/mdm/plants", new
            {
                plantId = id, plantName = name, country = "KR", timeZone = "Asia/Seoul"
            });
            seed.StatusCode.Should().Be(HttpStatusCode.OK, $"플랜트 시드({id})가 성공해야 한다");
        }

        // 등록 쿼리 실행(파라미터 없음) → 두 건 모두 조회.
        var all = await ExecuteQuery(client, "MDM.PlantList", new Dictionary<string, object?>());
        all.Should().Contain(r => Str(r, "PLANT_ID") == "P-Q-1");
        all.Should().Contain(r => Str(r, "PLANT_ID") == "P-Q-2");

        // 선택 필터(@plantId) 적용 → 한 건만.
        var filtered = await ExecuteQuery(client, "MDM.PlantList",
            new Dictionary<string, object?> { ["plantId"] = "P-Q-1" });
        filtered.Should().ContainSingle().Which.Should().ContainKey("PLANT_ID");
        Str(filtered[0], "PLANT_ID").Should().Be("P-Q-1");
        Str(filtered[0], "PLANT_NAME").Should().Be("Query Plant 1");
    }

    private static async Task<List<Dictionary<string, JsonElement>>> ExecuteQuery(
        HttpClient client, string queryId, Dictionary<string, object?> parameters)
    {
        var resp = await client.PostAsJsonAsync($"/api/v1/query/{queryId}", parameters);
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.OK, $"등록 쿼리 실행이 성공해야 한다. 응답 본문: {body}");
        return await resp.Content.ReadFromJsonAsync<List<Dictionary<string, JsonElement>>>()
            ?? new List<Dictionary<string, JsonElement>>();
    }

    private static string? Str(Dictionary<string, JsonElement> row, string col)
        => row.TryGetValue(col, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
