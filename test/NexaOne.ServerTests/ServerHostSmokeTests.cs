using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>통합 호스트(Phase 1) 웹 셸 스모크 — 모듈/플러그인 OFF로 ASP.NET 파이프라인만 검증한다.
/// /health는 익명 200, /diag는 인증 요구(토큰 없으면 401)로 인증 파이프라인 활성을 입증한다.
/// 플러그인 로드·9개 서비스·SQLite 스키마는 정적 ApplicationServer 싱글톤 제약으로 수동 기동 검증한다(플랜 Task 4).</summary>
public sealed class ServerHostSmokeTests : IClassFixture<ServerHostSmokeTests.ShellFactory>
{
    private readonly ShellFactory _factory;
    public ServerHostSmokeTests(ShellFactory factory) => _factory = factory;

    public sealed class ShellFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");   // 순수 웹 셸(플러그인/워커 OFF)
            builder.UseSetting("Jwt:SecretKey", "phase1-smoke-only-jwt-secret-key-at-least-32-bytes-long");
            builder.UseSetting("Jwt:Issuer", "nexaone-test");
            builder.UseSetting("Jwt:Audience", "nexaone-test");
        }
    }

    [Fact]
    public async Task Health_endpoint_is_anonymous_and_healthy()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/health");
        res.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Diag_requires_authentication_without_token()
    {
        var client = _factory.CreateClient();
        var res = await client.GetAsync("/diag");
        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "/diag는 RequireAuthorization으로 인증 파이프라인이 활성임을 입증한다");
    }
}
