using Microsoft.Extensions.DependencyInjection;
using NexaOne.EST.Application.Est;
using NexaOne.EST.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — 상태 슬라이스를 알람 애그리거트로 확장. EST 알람 발생/해제 시 도메인 이벤트가 알람 행과 '동일
/// 트랜잭션'에 EES_OUTBOX로 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 발생/해제 각 1행, 비활성(기본)이면
/// 0행. outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. (테스트 SQLite는 FK off라
/// 미등록 설비 알람 INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class EstAlarmOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public EstAlarmOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 알람 발생(도메인 이벤트 발행)을 리포의 트랜잭션 경로로 기록한다.
    private static async Task RaiseAsync(IServiceProvider sp, string alarmId, string equipmentId)
    {
        var repo = sp.GetRequiredService<IEquipmentAlarmRepository>();
        var alarm = EquipmentAlarm.Create(alarmId, equipmentId, "INTERLOCK_STOP", "[FDC] 인터록", "CRITICAL", DateTime.UtcNow).Value;
        await repo.AddAsync(alarm);   // EquipmentAlarmRaisedDomainEvent 발행
    }

    [Fact]
    public async Task Alarm_raise_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        await RaiseAsync(scope.ServiceProvider, "AL-OB-ON", "EQ-AL-ON");

        var persisted = await scope.ServiceProvider.GetRequiredService<IEquipmentAlarmRepository>().GetByIdAsync("AL-OB-ON");
        persisted.Should().NotBeNull("알람이 영속돼야 한다(트랜잭션 커밋)");

        OutboxCount(On.ConnectionString, "EQ-AL-ON", "EquipmentAlarmRaised").Should().Be(1,
            "outbox 활성 시 알람 발생과 같은 트랜잭션에 EquipmentAlarmRaised 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Alarm_clear_writes_cleared_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEquipmentAlarmRepository>();
        await RaiseAsync(scope.ServiceProvider, "AL-OB-CLR", "EQ-AL-CLR");

        var alarm = await repo.GetByIdAsync("AL-OB-CLR");   // Restore — 이벤트 없음
        alarm!.Clear(DateTime.UtcNow);                       // EquipmentAlarmClearedDomainEvent 발행
        await repo.UpdateAsync(alarm);

        OutboxCount(On.ConnectionString, "EQ-AL-CLR", "EquipmentAlarmCleared").Should().Be(1,
            "outbox 활성 시 알람 해제와 같은 트랜잭션에 EquipmentAlarmCleared 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Alarm_raise_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        await RaiseAsync(scope.ServiceProvider, "AL-OB-OFF", "EQ-AL-OFF");

        var persisted = await scope.ServiceProvider.GetRequiredService<IEquipmentAlarmRepository>().GetByIdAsync("AL-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 알람은 정상 기록돼야 한다");

        OutboxCount(Off.ConnectionString, "EQ-AL-OFF", "EquipmentAlarmRaised").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
