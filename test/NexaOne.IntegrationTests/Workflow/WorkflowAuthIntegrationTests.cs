using System.Net;
using System.Net.Http.Json;

namespace NexaOne.IntegrationTests.Workflow;

/// <summary>
/// A-2 회귀 — 워크플로우 실행은 등록 노드를 구동하므로 perm:workflow:execute 권한을 요구한다.
/// 권한 없는(다른 권한만 가진) 인증 사용자는 403이어야 한다(인증만으로 임의 워크플로우 실행 불가).
/// </summary>
public sealed class WorkflowAuthIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public WorkflowAuthIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Execute_without_workflow_permission_is_forbidden()
    {
        // workflow:execute가 아닌 권한만 보유한 토큰.
        var client = _factory.CreateAuthenticatedClient("fdc:read");

        var resp = await client.PostAsJsonAsync("/api/v1/workflow/any-id/execute", new { });

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "workflow:execute 권한이 없으면 워크플로우 실행은 403이어야 한다(인가가 라우팅/핸들러보다 우선)");
    }

    [Fact]
    public async Task Execute_requires_authentication()
    {
        var anon = _factory.CreateClient();
        var resp = await anon.PostAsJsonAsync("/api/v1/workflow/any-id/execute", new { });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "토큰 없으면 401");
    }
}
