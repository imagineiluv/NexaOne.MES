using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.SYS;

/// <summary>쿼리 카탈로그 엔드포인트 — 디자이너 드롭다운/UX 권한의 출처.
/// 등록된 쿼리를 {id, isWrite, requiredPermission}로 노출하되 SQL은 절대 노출하지 않는다.
/// 관리 권한(perm:sys:manage)으로만 접근 가능.</summary>
public sealed class QueryCatalogIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;
    public QueryCatalogIntegrationTests(TestApiFactory factory) => _factory = factory;

    private sealed record QueryDescriptorDto(string Id, bool IsWrite, string? RequiredPermission);

    [Fact]
    public async Task Lists_registered_queries_with_kind_and_permission_but_no_sql()
    {
        var client = _factory.CreateAuthenticatedClient("sys:manage");

        var res = await client.GetAsync("/api/v1/sys/queries");
        res.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await res.Content.ReadAsStringAsync();
        body.Should().NotContain("SELECT", "SQL 본문은 카탈로그에 노출되면 안 된다");
        body.Should().NotContain("INSERT");

        var items = await res.Content.ReadFromJsonAsync<List<QueryDescriptorDto>>();
        items.Should().NotBeNull();
        items!.Should().Contain(d => d.Id == "MDM.PlantList" && d.IsWrite == false);
        items.Should().Contain(d => d.Id == "MDM.CreatePlant" && d.IsWrite == true && d.RequiredPermission == "mdm:manage");
    }

    [Fact]
    public async Task Forbids_without_sys_manage_permission()
    {
        var client = _factory.CreateAuthenticatedClient("fdc:read");   // sys:manage 없음
        var res = await client.GetAsync("/api/v1/sys/queries");
        res.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
