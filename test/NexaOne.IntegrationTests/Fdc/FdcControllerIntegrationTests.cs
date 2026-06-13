using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FdcController HTTP 통합 테스트 골격 — WebApplicationFactory로 API를 인프로세스 기동해
/// 인증·엔드포인트 라우팅·직렬화를 검증한다 (design 10.4.1 / 18.2.3).
/// </summary>
/// <remarks>
/// TestApiFactory가 SQLite(임시 파일 DB) + 마이그레이션 스키마로 인프로세스 기동하므로 실 MSSQL 없이
/// 실행된다. 토큰은 앱 JwtService로 발급한다(시드 사용자 불필요).
/// </remarks>
public sealed class FdcControllerIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FdcControllerIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task ProtectedFdcEndpoint_returns_401_without_token()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/fdc/parameter-groups?equipmentId=EQ-001");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "[Authorize] 컨트롤러는 토큰 없이 401을 반환해야 한다");
    }

    [Fact]
    public async Task GetParameterGroups_returns_ok_with_valid_token()
    {
        var client = _factory.CreateAuthenticatedClient();   // JwtService로 유효 토큰 발급(SQLite 백엔드)

        var response = await client.GetAsync("/api/v1/fdc/parameter-groups?equipmentId=EQ-001");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "유효 토큰 + SQLite 스키마에서 빈 결과(200)를 반환해야 한다");
    }
}
