using Microsoft.Extensions.DependencyInjection;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — EST 알람 슬라이스를 FDC로 미러링. FDC 알람 발생/해제 시 도메인 이벤트가 알람 이력 행과 '동일
/// 트랜잭션'에 EES_OUTBOX로 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 발생/해제 각 1행, 비활성(기본)이면
/// 0행. 해제 검증은 GetOpenAsync로 '재조회 후 변경'하여(Restore 경로) 읽기경로 유령 이벤트 회귀가 있으면
/// COUNT>1로 드러나게 한다. outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다.
/// (테스트 SQLite는 FK off라 미등록 설비 알람 INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class FdcFdcAlarmHistoryOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public FdcFdcAlarmHistoryOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 알람 발생(도메인 이벤트 발행)을 리포의 트랜잭션 경로로 기록한다.
    private static async Task RaiseAsync(IServiceProvider sp, string alarmId, string equipmentId)
    {
        var repo = sp.GetRequiredService<IFdcAlarmHistoryRepository>();
        var alarm = FdcAlarmHistory.Create(
            alarmId, "CFG-1", equipmentId, "PARAM-1", "CRITICAL", 42.0m, "[FDC] 임계 초과", DateTime.UtcNow).Value;
        await repo.AddAsync(alarm);   // FdcAlarmRaisedDomainEvent 발행
    }

    [Fact]
    public async Task Alarm_raise_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        await RaiseAsync(scope.ServiceProvider, "FDC-AL-OB-ON", "EQ-FDC-AL-ON");

        var open = await scope.ServiceProvider.GetRequiredService<IFdcAlarmHistoryRepository>()
            .GetOpenAsync("EQ-FDC-AL-ON");
        open.Should().ContainSingle(a => a.Id == "FDC-AL-OB-ON", "알람이 영속돼야 한다(트랜잭션 커밋)");

        OutboxCount(On.ConnectionString, "EQ-FDC-AL-ON", "FdcAlarmRaised").Should().Be(1,
            "outbox 활성 시 알람 발생과 같은 트랜잭션에 FdcAlarmRaised 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Alarm_clear_writes_cleared_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFdcAlarmHistoryRepository>();
        await RaiseAsync(scope.ServiceProvider, "FDC-AL-OB-CLR", "EQ-FDC-AL-CLR");

        // 재조회(Restore 경로) 후 변경 — 읽기경로가 유령 이벤트를 발행하면 COUNT>1로 드러난다.
        var alarm = (await repo.GetOpenAsync("EQ-FDC-AL-CLR")).Single(a => a.Id == "FDC-AL-OB-CLR");
        alarm.Clear(DateTime.UtcNow);    // FdcAlarmClearedDomainEvent 발행
        await repo.UpdateAsync(alarm);

        OutboxCount(On.ConnectionString, "EQ-FDC-AL-CLR", "FdcAlarmCleared").Should().Be(1,
            "outbox 활성 시 알람 해제와 같은 트랜잭션에 FdcAlarmCleared 이벤트가 1건 기록돼야 한다");
        OutboxCount(On.ConnectionString, "EQ-FDC-AL-CLR", "FdcAlarmRaised").Should().Be(1,
            "재조회는 Restore 경로라 발생 이벤트를 재발행하지 않아야 한다(유령 이벤트 회귀 방지)");
    }

    [Fact]
    public async Task Alarm_raise_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        await RaiseAsync(scope.ServiceProvider, "FDC-AL-OB-OFF", "EQ-FDC-AL-OFF");

        var open = await scope.ServiceProvider.GetRequiredService<IFdcAlarmHistoryRepository>()
            .GetOpenAsync("EQ-FDC-AL-OFF");
        open.Should().ContainSingle(a => a.Id == "FDC-AL-OB-OFF", "outbox 비활성이어도 알람은 정상 기록돼야 한다");

        OutboxCount(Off.ConnectionString, "EQ-FDC-AL-OFF", "FdcAlarmRaised").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
