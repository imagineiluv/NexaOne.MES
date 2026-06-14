using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 대표 슬라이스 — EST 설비 상태 변경 시 도메인 이벤트가 상태/이력과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성(OutboxEnabledTestApiFactory)이면 outbox 1행, 비활성(기본
/// TestApiFactory)이면 0행(적체 없음). outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다.
/// </summary>
public sealed class EstOutboxIntegrationTests
    : IClassFixture<TestApiFactory>, IClassFixture<OutboxEnabledTestApiFactory>
{
    private readonly TestApiFactory _off;
    private readonly OutboxEnabledTestApiFactory _on;

    public EstOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
    {
        _off = off;
        _on = on;
    }

    // 상태 전이(도메인 이벤트 발행) + 이력을 리포의 트랜잭션 경로로 기록한다.
    private static async Task ChangeStateAsync(IServiceProvider sp, string equipmentId, string toState)
    {
        var repo = sp.GetRequiredService<IEquipmentStateRepository>();
        var state = EquipmentCurrentState.Create(equipmentId, "P-OB", "IDLE");
        state.ApplyTransition(toState);   // EquipmentStateChangedDomainEvent 발행
        var hist = EquipmentStateHistory.Create(
            $"{equipmentId}-H1", equipmentId, "IDLE", toState, toState,
            state.StateChangedAt, "tester", "test", "UI").Value;
        await repo.ChangeStateWithHistoryAsync(state, hist);
    }

    // EES_OUTBOX를 직접 조회(발행/미발행 무관) — 디스패처 타이밍에 의존하지 않는 결정론적 검증.
    private static int OutboxCount(string connectionString, string aggregateId)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID = $id AND EVENT_TYPE = 'EquipmentStateChanged'";
        cmd.Parameters.AddWithValue("$id", aggregateId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task State_change_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = _on.Services.CreateScope();
        await ChangeStateAsync(scope.ServiceProvider, "EQ-OB-ON", "RUNNING");

        // 상태가 영속됨(트랜잭션 커밋) + 동일 트랜잭션에 outbox 이벤트 1건.
        var state = await scope.ServiceProvider.GetRequiredService<IEquipmentStateRepository>().GetAsync("EQ-OB-ON");
        state.Should().NotBeNull();
        state!.CurrentStateId.Should().Be("RUNNING", "상태 변경이 영속돼야 한다");

        OutboxCount(_on.ConnectionString, "EQ-OB-ON").Should().Be(1,
            "outbox 활성 시 상태 변경과 같은 트랜잭션에 EquipmentStateChanged 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task State_change_does_not_write_outbox_when_disabled()
    {
        using var scope = _off.Services.CreateScope();
        await ChangeStateAsync(scope.ServiceProvider, "EQ-OB-OFF", "RUNNING");

        var state = await scope.ServiceProvider.GetRequiredService<IEquipmentStateRepository>().GetAsync("EQ-OB-OFF");
        state!.CurrentStateId.Should().Be("RUNNING", "outbox 비활성이어도 상태/이력은 정상 기록돼야 한다");

        OutboxCount(_off.ConnectionString, "EQ-OB-OFF").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
