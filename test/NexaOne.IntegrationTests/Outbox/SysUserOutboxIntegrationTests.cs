using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.SYS.Application.Users;
using NexaOne.SYS.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — 사용자(User) 애그리거트의 트랜잭션 outbox. 비활성화 시 도메인 이벤트가 사용자 행과 '동일 트랜잭션'에
/// EES_OUTBOX로 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 비활성화 1행, 비활성(기본)이면 0행.
/// outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속 후 GetById로 다시
/// 로드해 전이→저장하므로(reload-then-mutate), 읽기경로가 Restore 아닌 Create+재생이면 팬텀 이벤트가 COUNT>1로 드러난다.
/// (로그인 실패 잠금은 원자 SQL UPDATE가 도메인 이벤트 수집을 우회하는 별도 경로이며 이 슬라이스의 범위 밖이다.)
/// (테스트 SQLite는 FK off라 미등록 부모 없이 사용자 INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class SysUserOutboxIntegrationTests
    : IClassFixture<TestApiFactory>, IClassFixture<OutboxEnabledTestApiFactory>
{
    private readonly TestApiFactory _off;
    private readonly OutboxEnabledTestApiFactory _on;

    public SysUserOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
    {
        _off = off;
        _on = on;
    }

    // 활성 사용자를 영속한다(생성은 이벤트를 발행하지 않음 — 비활성화 전이만 발행).
    private static async Task SeedActiveAsync(IServiceProvider sp, string userId)
    {
        var repo = sp.GetRequiredService<IUserRepository>();
        var user = User.Create(userId, "User-OB", "hash-OB", "ob@test.com", "USER").Value;
        await repo.AddAsync(user);
    }

    // EES_OUTBOX를 직접 조회(발행/미발행 무관) — 디스패처 타이밍에 의존하지 않는 결정론적 검증.
    private static int OutboxCount(string connectionString, string aggregateId, string eventType)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID = $id AND EVENT_TYPE = $type";
        cmd.Parameters.AddWithValue("$id", aggregateId);
        cmd.Parameters.AddWithValue("$type", eventType);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Deactivate_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = _on.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await SeedActiveAsync(scope.ServiceProvider, "USR-OB-DEACT");

        var user = await repo.GetByIdAsync("USR-OB-DEACT");   // Restore — 이벤트 없음
        user!.Deactivate();                                    // UserDeactivatedDomainEvent 발행
        await repo.UpdateAsync(user);

        OutboxCount(_on.ConnectionString, "USR-OB-DEACT", "UserDeactivated").Should().Be(1,
            "outbox 활성 시 비활성화와 같은 트랜잭션에 UserDeactivated 이벤트가 1건 기록돼야 한다(재로드 후 전이라 팬텀이면 >1)");
    }

    [Fact]
    public async Task Deactivate_does_not_write_outbox_when_disabled()
    {
        using var scope = _off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
        await SeedActiveAsync(scope.ServiceProvider, "USR-OB-OFF");

        var user = await repo.GetByIdAsync("USR-OB-OFF");
        user!.Deactivate();
        await repo.UpdateAsync(user);

        var persisted = await repo.GetByIdAsync("USR-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 사용자는 정상 기록돼야 한다");
        persisted!.IsActive.Should().BeFalse("비활성 경로에서도 상태 전이는 영속돼야 한다");
        persisted.IsDeleted.Should().BeTrue();

        OutboxCount(_off.ConnectionString, "USR-OB-OFF", "UserDeactivated").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
