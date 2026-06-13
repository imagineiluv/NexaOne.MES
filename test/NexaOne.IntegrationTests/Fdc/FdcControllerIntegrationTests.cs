using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FdcController HTTP 통합 테스트 골격 — WebApplicationFactory로 API를 인프로세스 기동해
/// 인증·엔드포인트 라우팅·직렬화를 검증한다 (design 10.4.1 / 18.2.3).
/// </summary>
/// <remarks>
/// 보호된 엔드포인트 호출은 JWT 토큰 발급(SYS 인증)과 실 MSSQL 연결이 필요하므로 기본 실행에서는
/// <c>Skip</c>한다. testcontainers(MSSQL) + 시드 사용자/토큰이 갖춰진 CI에서 Skip을 제거해 실행한다.
/// </remarks>
public sealed class FdcControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FdcControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact(Skip = "실 MSSQL + JWT 인증 필요 — 환경 구축된 CI에서 Skip 제거 후 실행")]
    public async Task ProtectedFdcEndpoint_returns_401_without_token()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/fdc/parameter-groups?equipmentId=EQ-001");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] 컨트롤러는 토큰 없이 401을 반환해야 한다");
    }

    [Fact(Skip = "실 MSSQL + JWT 인증 필요 — 환경 구축된 CI에서 Skip 제거 후 실행")]
    public async Task GetParameterGroups_returns_ok_with_valid_token()
    {
        var client = _factory.CreateClient();
        // var token = await IssueTokenAsync(client);   // SYS 로그인으로 JWT 발급 (CI에서 시드 사용자 사용)
        // client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        var response = await client.GetAsync("/api/v1/fdc/parameter-groups?equipmentId=EQ-001");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
