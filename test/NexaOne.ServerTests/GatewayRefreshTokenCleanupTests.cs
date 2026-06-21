using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Server.Gateway;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>리프레시 토큰 만료 정리(후속) — SysRefreshTokenStore.PurgeExpiredAsync 동작 + 워커 disabled no-op.
/// modules OFF + SQLite(NexaMes 스키마, V034 포함). 정리 로직을 store에서 직접 검증해 워커 타이밍 의존을 회피한다.
/// (1) 만료 정리: EXPIRES_AT 과거화 → PurgeExpiredAsync(Zero) ≥1 → ValidateAsync false.
/// (2) 활성 보존: 새 토큰 → PurgeExpiredAsync(7d) → ValidateAsync true.
/// (3) 워커 disabled: enabled=false → StartAsync 즉시 완료, StopAsync 예외 없음.</summary>
public sealed class GatewayRefreshTokenCleanupTests : IClassFixture<GatewayRefreshTokenCleanupTests.CleanupFactory>
{
    private const string Secret = "phase3b-auth-e2e-jwt-secret-key-at-least-32-bytes!!";
    private const string Issuer = "nexaone-cleanup-test";
    private readonly CleanupFactory _factory;
    public GatewayRefreshTokenCleanupTests(CleanupFactory factory) => _factory = factory;

    public sealed class CleanupFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-rt-cleanup-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", Secret);
            builder.UseSetting("Jwt:Issuer", Issuer);
            builder.UseSetting("Jwt:Audience", Issuer);
            builder.UseSetting("RateLimiting:Enabled", "false");
            // 정리 워커는 기본 OFF 그대로 둔다(테스트는 store를 직접 호출해 타이밍 의존 회피).
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시파일 정리 실패 무시 */ }
        }
    }

    // 호스트를 한 번 띄워 SQLite 스키마(+admin 시드)를 보장한다.
    private void EnsureSchemaReady() => _ = _factory.CreateClient();

    private static string Uid(string p) => $"{p}_{Guid.NewGuid():N}".Substring(0, 16);

    // 한 사용자의 모든 토큰 EXPIRES_AT를 과거(now-30d)로 만든다(테스트 전용 사용자라 일괄 UPDATE 안전).
    // SQLite datetime('now','-30 days')는 'yyyy-MM-dd HH:mm:ss' — Microsoft.Data.Sqlite의 DateTime 저장 포맷과
    // 사전식 비교 등가라 DELETE/Validate 비교가 정상 동작한다.
    private void ExpireUserTokens(string userId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE SYS_REFRESH_TOKEN SET EXPIRES_AT = datetime('now','-30 days') WHERE USER_ID = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        cmd.ExecuteNonQuery();
    }

    private int CountUserTokens(string userId)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SYS_REFRESH_TOKEN WHERE USER_ID = @id";
        cmd.Parameters.AddWithValue("@id", userId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private SysRefreshTokenStore ResolveStore()
        => _factory.Services.GetRequiredService<SysRefreshTokenStore>();

    [Fact]
    public async Task PurgeExpiredAsync_deletes_expired_token_and_validate_returns_false()
    {
        EnsureSchemaReady();
        var store = ResolveStore();
        var uid = Uid("exp");

        var token = await store.IssueAsync(uid);
        (await store.ValidateAsync(uid, token)).Should().BeTrue("발급 직후 토큰은 유효해야 한다");

        ExpireUserTokens(uid); // EXPIRES_AT를 과거로
        var deleted = await store.PurgeExpiredAsync(TimeSpan.Zero); // cutoff = now → 과거 토큰 삭제

        deleted.Should().BeGreaterThanOrEqualTo(1, "만료된 토큰이 1건 이상 삭제돼야 한다");
        (await store.ValidateAsync(uid, token)).Should().BeFalse("삭제된 토큰은 더 이상 유효하지 않아야 한다");
        CountUserTokens(uid).Should().Be(0, "해당 사용자의 만료 토큰 행이 제거돼야 한다");
    }

    [Fact]
    public async Task PurgeExpiredAsync_preserves_active_token()
    {
        EnsureSchemaReady();
        var store = ResolveStore();
        var uid = Uid("act");

        var token = await store.IssueAsync(uid); // EXPIRES_AT = now + ttl(미래)
        await store.PurgeExpiredAsync(TimeSpan.FromDays(7)); // cutoff = now-7d → 미래 토큰 보존

        (await store.ValidateAsync(uid, token)).Should().BeTrue("미만료 활성 토큰은 보존돼야 한다");
        CountUserTokens(uid).Should().Be(1, "활성 토큰 행이 그대로 남아야 한다");
    }

    [Fact]
    public async Task Worker_disabled_starts_and_stops_without_error_as_noop()
    {
        EnsureSchemaReady();
        var store = ResolveStore();

        var worker = new RefreshTokenCleanupWorker(store, enabled: false, interval: TimeSpan.FromSeconds(1), retention: TimeSpan.Zero);

        // disabled면 ExecuteAsync가 즉시 return → StartAsync는 곧바로 완료된다.
        var start = worker.StartAsync(CancellationToken.None);
        await start.WaitAsync(TimeSpan.FromSeconds(5)); // 무한 대기 방지
        start.IsCompletedSuccessfully.Should().BeTrue("disabled 워커는 시작 즉시 no-op으로 완료돼야 한다");

        var stop = async () => await worker.StopAsync(CancellationToken.None);
        await stop.Should().NotThrowAsync("disabled 워커 정지는 예외 없이 끝나야 한다");

        worker.Dispose();
    }
}
