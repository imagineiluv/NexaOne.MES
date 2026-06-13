using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace NexaOne.IntegrationTests;

/// <summary>
/// API 인프로세스 기동용 WebApplicationFactory. 부팅 fail-fast(§18.7 — JWT 비밀키 강도 검증)를
/// 통과하도록 테스트 전용 강한 Jwt:SecretKey를 주입한다. 실 DB/인증/설비는 여전히 환경 의존이므로
/// 보호된 엔드포인트 호출 테스트는 Skip 상태로 둔다.
/// </summary>
public sealed class TestApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "integration-test-only-jwt-secret-key-at-least-32-bytes-long",
                ["Jwt:Issuer"] = "nexaone-test",
                ["Jwt:Audience"] = "nexaone-test",
            });
        });
    }
}
