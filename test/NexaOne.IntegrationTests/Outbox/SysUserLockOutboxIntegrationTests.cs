using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — SYS 계정 잠금 이벤트. 잠금 전이는 동시성 안전을 위해 원자 SQL UPDATE(RecordLoginFailureAsync)가
/// 소유하므로, 리포가 같은 트랜잭션에 조건부로 EES_OUTBOX UserAccountLocked를 '전이 시 1건만' 기록한다.
/// 활성 시: 임계 도달 호출에 1건, 이미 잠긴 뒤 추가 실패엔 미발행(중복 없음). 비활성 시: 0건(잠금 자체는 정상 동작).
/// </summary>
public sealed class SysUserLockOutboxIntegrationTests
    : IClassFixture<TestApiFactory>, IClassFixture<OutboxEnabledTestApiFactory>
{
    private readonly TestApiFactory _off;
    private readonly OutboxEnabledTestApiFactory _on;

    public SysUserLockOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
    {
        _off = off;
        _on = on;
    }

    private static async Task SeedUserAsync(IServiceProvider sp, string userId)
    {
        var repo = sp.GetRequiredService<IUserRepository>();
        var user = User.Create(userId, $"name-{userId}", "hash", $"{userId}@x.com", "ROLE1").Value;
        await repo.AddAsync(user);
    }

    private static int LockEventCount(string connectionString, string userId)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID = $id AND EVENT_TYPE = 'UserAccountLocked'";
        cmd.Parameters.AddWithValue("$id", userId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Lock_transition_writes_single_outbox_event_when_enabled()
    {
        using var scope = _on.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await SeedUserAsync(scope.ServiceProvider, "U-LOCK-ON");

        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime? locked = null;
        for (var i = 0; i < User.MaxConsecutiveFailures; i++)
            locked = await repo.RecordLoginFailureAsync("U-LOCK-ON", now);

        locked.Should().NotBeNull("임계 연속 실패 시 계정이 잠겨야 한다");
        LockEventCount(_on.ConnectionString, "U-LOCK-ON").Should().Be(1,
            "잠금 전이 호출에 EES_OUTBOX UserAccountLocked가 1건 기록돼야 한다");

        // 이미 잠긴 뒤 추가 실패는 중복 발행하지 않는다(FAIL_COUNT가 임계를 넘어 조건 불일치).
        await repo.RecordLoginFailureAsync("U-LOCK-ON", now);
        LockEventCount(_on.ConnectionString, "U-LOCK-ON").Should().Be(1,
            "이미 잠긴 계정의 추가 실패는 outbox 이벤트를 중복 발행하지 않아야 한다");
    }

    [Fact]
    public async Task Lock_writes_no_outbox_event_when_disabled()
    {
        using var scope = _off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await SeedUserAsync(scope.ServiceProvider, "U-LOCK-OFF");

        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime? locked = null;
        for (var i = 0; i < User.MaxConsecutiveFailures; i++)
            locked = await repo.RecordLoginFailureAsync("U-LOCK-OFF", now);

        locked.Should().NotBeNull("outbox 비활성이어도 잠금 자체는 정상 동작해야 한다");
        LockEventCount(_off.ConnectionString, "U-LOCK-OFF").Should().Be(0,
            "outbox 비활성(기본)에서는 잠금 이벤트를 기록하지 않아야 한다");
    }
}
