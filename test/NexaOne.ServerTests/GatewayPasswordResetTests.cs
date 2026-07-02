using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>forgot/reset-password E2E — modules OFF + SQLite. IEmailSender를 레코더로 대체해 전 흐름을 실검증한다:
/// forgot(익명, 열거방지 항상 200) → 메일로 전달된 토큰 추출(DB에는 SHA-256 해시만, V065) → reset(정책검증+1회용) →
/// 새 비밀번호 로그인 성공·기존 비밀번호 401·토큰 재사용 400. 폐기 NexaOne.API PasswordResetService 갭 복원 검증.</summary>
public sealed class GatewayPasswordResetTests : IClassFixture<GatewayPasswordResetTests.ResetFactory>
{
    private readonly ResetFactory _factory;
    public GatewayPasswordResetTests(ResetFactory factory) => _factory = factory;

    /// <summary>발송 메일을 기록하는 IEmailSender — DI 마지막 등록이 이겨 실제 발송 대신 캡처된다.</summary>
    public sealed class RecordingEmailSender : IEmailSender
    {
        public readonly List<(string To, string Subject, string Body)> Sent = new();
        public Task SendAsync(string to, string subject, string body, CancellationToken ct = default)
        {
            lock (Sent) Sent.Add((to, subject, body));
            return Task.CompletedTask;
        }
    }

    public sealed class ResetFactory : WebApplicationFactory<Program>
    {
        public readonly RecordingEmailSender Emails = new();
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-pwreset-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("RateLimiting:Enabled", "false");   // 흐름 검증 — 레이트리밋 비활성
            builder.UseSetting("Jwt:SecretKey", "pw-reset-e2e-jwt-secret-key-32bytes+!!!!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-pwreset-test");
            builder.UseSetting("Jwt:Audience", "nexaone-pwreset-test");
            builder.ConfigureServices(services => services.AddSingleton<IEmailSender>(Emails));
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    [Fact]
    public async Task Full_reset_flow_token_email_reset_login()
    {
        var client = _factory.CreateClient();
        const string newPassword = "Str0ng!Reset#2026";

        // 1) forgot — 익명 200, 메일 레코더에 토큰 전달(V001 admin@nexaone.local).
        var forgot = await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { userId = "admin" });
        forgot.StatusCode.Should().Be(HttpStatusCode.OK);
        (string To, string Subject, string Body) mail;
        lock (_factory.Emails.Sent) mail = _factory.Emails.Sent.Single(m => m.To == "admin@nexaone.local");
        var token = Regex.Match(mail.Body, "토큰: ([0-9A-F]+)").Groups[1].Value;
        token.Should().NotBeNullOrEmpty("메일 본문에 재설정 토큰 원문이 있어야 한다(DB에는 해시만)");

        // 2) reset — 토큰 + 새 비밀번호(정책 통과) → 200.
        var reset = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token, newPassword, confirmPassword = newPassword });
        reset.StatusCode.Should().Be(HttpStatusCode.OK, await reset.Content.ReadAsStringAsync());

        // 3) 새 비밀번호 로그인 성공, 기존(admin) 401.
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = "admin", password = newPassword }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "재설정된 비밀번호로 로그인돼야 한다");
        (await client.PostAsJsonAsync("/api/v1/auth/login", new { userId = "admin", password = "admin" }))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized, "기존 비밀번호는 무효");

        // 4) 토큰 재사용 → 400 (1회용).
        var reuse = await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token, newPassword = "An0ther!Pass#99", confirmPassword = "An0ther!Pass#99" });
        reuse.StatusCode.Should().Be(HttpStatusCode.BadRequest, "재설정 토큰은 1회용이어야 한다");
    }

    [Fact]
    public async Task Forgot_unknown_user_returns_ok_without_mail_and_invalid_token_rejected()
    {
        var client = _factory.CreateClient();

        // 미존재 사용자도 200(열거 방지) + 메일 미발송.
        int before; lock (_factory.Emails.Sent) before = _factory.Emails.Sent.Count;
        (await client.PostAsJsonAsync("/api/v1/auth/forgot-password", new { userId = $"ghost_{Guid.NewGuid():N}" }))
            .StatusCode.Should().Be(HttpStatusCode.OK, "존재 여부를 응답으로 노출하지 않는다");
        lock (_factory.Emails.Sent) _factory.Emails.Sent.Count.Should().Be(before, "미존재 사용자에겐 발송하지 않는다");

        // 위조 토큰 → 400.
        (await client.PostAsJsonAsync("/api/v1/auth/reset-password",
            new { token = "DEADBEEF", newPassword = "Str0ng!X#2026", confirmPassword = "Str0ng!X#2026" }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
