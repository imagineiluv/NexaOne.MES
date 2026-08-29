using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using NexaOne.Infrastructure.Diagnostics;
using NexaOne.Server;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>운영 기본 자격 하드닝 가드 — dev 시드 admin(V001 기본 SHA-256 해시, PASSWORD_STATE=Normal)에
/// startup lifecycle을 적용하면 'Create'로 전이되고 재적용은 0행(멱등), 이후 로그인은
/// requirePasswordChange=true가 된다(기존 강제변경 미들웨어 흐름 편승). Hosted service는 Production에서만 실행
/// — Development 부팅(전 테스트)이 admin/admin 흐름을 그대로 쓰는 것과 양립함을 이 테스트가 보증한다.</summary>
public sealed class DefaultAdminHardeningTests : IClassFixture<DefaultAdminHardeningTests.HardeningFactory>
{
    private readonly HardeningFactory _factory;
    public DefaultAdminHardeningTests(HardeningFactory factory) => _factory = factory;

    public sealed class HardeningFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-admin-harden-{Guid.NewGuid():N}.db");
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", $"Data Source={DbPath};Foreign Keys=False");
            builder.UseSetting("Jwt:SecretKey", "admin-hardening-test-jwt-secret-32bytes!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-harden-test");
            builder.UseSetting("Jwt:Audience", "nexaone-harden-test");
            builder.UseSetting("RateLimiting:Enabled", "false");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 무시 */ }
        }
    }

    private sealed record LoginBody(string AccessToken, bool RequirePasswordChange);

    [Fact]
    public async Task Startup_lifecycle_hardens_default_admin_and_forces_password_change()
    {
        using var client = _factory.CreateClient();   // 부팅 확정(스키마+dev admin 시드)

        // 표준 RunAsync도 실행하는 Production startup lifecycle을 직접 구동한다.
        var startup = new NexaOneMesStartupHostedService(
            _factory.Services,
            new ProductionHostEnvironment(),
            _factory.Services.GetRequiredService<NexaOneMesRuntimeState>(),
            _factory.Services.GetRequiredService<ExternalDependencyProbeCatalog>());
        await startup.StartAsync(CancellationToken.None);

        // 이미 lifecycle이 전이했으므로 직접 재적용은 0행(멱등 — Create 상태).
        (await DefaultAdminHardening.HardenAsync(_factory.Services)).Should().Be(0, "재적용은 무변경(멱등)");

        // 로그인은 여전히 성공하되 requirePasswordChange=true — 기존 미들웨어가 업무 API를 차단하는 상태.
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = "admin", password = "admin" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<LoginBody>();
        body!.RequirePasswordChange.Should().BeTrue("PASSWORD_STATE=Create는 첫 로그인 시 변경 강제");
    }

    private sealed class ProductionHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "NexaOne.ServerTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
