using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>운영 기본 자격 하드닝 가드 — dev 시드 admin(V001 기본 SHA-256 해시, PASSWORD_STATE=Normal)에
/// HardenAsync를 적용하면 'Create'로 전이되고(1행), 재적용은 0행(멱등), 이후 로그인은
/// requirePasswordChange=true가 된다(기존 강제변경 미들웨어 흐름 편승). Program은 Production에서만 호출
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
    public async Task Harden_flips_default_admin_to_create_once_and_forces_password_change()
    {
        using var client = _factory.CreateClient();   // 부팅 확정(스키마+dev admin 시드)

        // dev 시드 admin = 기본 해시 + Normal → 1행 전이, 재호출은 0행(멱등 — 이미 Create).
        (await DefaultAdminHardening.HardenAsync(_factory.Services)).Should().Be(1, "기본 해시+Normal인 admin은 강제변경 대상");
        (await DefaultAdminHardening.HardenAsync(_factory.Services)).Should().Be(0, "재적용은 무변경(멱등)");

        // 로그인은 여전히 성공하되 requirePasswordChange=true — 기존 미들웨어가 업무 API를 차단하는 상태.
        var res = await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = "admin", password = "admin" });
        res.EnsureSuccessStatusCode();
        var body = await res.Content.ReadFromJsonAsync<LoginBody>();
        body!.RequirePasswordChange.Should().BeTrue("PASSWORD_STATE=Create는 첫 로그인 시 변경 강제");
    }
}
