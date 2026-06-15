using Microsoft.Extensions.DependencyInjection;
using NexaOne.FDC.Application.Fdc;
using NexaOne.FDC.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — FDC 인터락 발동/해제 시 도메인 이벤트가 이력 행과 '동일 트랜잭션'에 EES_OUTBOX로 기록되는지(opt-in)를
/// SQLite에서 검증한다. 활성이면 발동/해제 각 1행, 비활성(기본)이면 0행. outbox 행은 디스패처 발행 여부와 무관하게
/// DB를 직접 조회해 결정론적으로 확인한다. 해제 검증은 영속 후 리포로 RELOAD한 뒤 전이하므로, 읽기경로가 PHANTOM
/// 이벤트를 발행하면 COUNT>1로 회귀가 드러난다. (테스트 SQLite는 FK off라 미등록 설비 이력 INSERT가 가능 —
/// 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class FdcInterlockHistoryOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public FdcInterlockHistoryOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 인터락 발동(도메인 이벤트 발행)을 리포의 트랜잭션 경로로 기록한다.
    private static async Task TriggerAsync(IServiceProvider sp, string historyId, string equipmentId)
    {
        var repo = sp.GetRequiredService<IFdcInterlockHistoryRepository>();
        var history = FdcInterlockHistory.Create(
            historyId, "RULE-OB", equipmentId, "PARAM-OB", 99m, "STOP", "[FDC] 인터록", DateTime.UtcNow).Value;
        await repo.AddAsync(history);   // FdcInterlockTriggeredDomainEvent 발행
    }

    [Fact]
    public async Task Interlock_trigger_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        await TriggerAsync(scope.ServiceProvider, "IH-OB-ON", "EQ-IH-ON");

        var repo = scope.ServiceProvider.GetRequiredService<IFdcInterlockHistoryRepository>();
        var persisted = await repo.GetUnresolvedAsync("EQ-IH-ON");
        persisted.Should().ContainSingle("인터락 이력이 영속돼야 한다(트랜잭션 커밋)");

        OutboxCount(On.ConnectionString, "EQ-IH-ON", "FdcInterlockTriggered").Should().Be(1,
            "outbox 활성 시 인터락 발동과 같은 트랜잭션에 FdcInterlockTriggered 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Interlock_resolve_writes_resolved_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFdcInterlockHistoryRepository>();
        await TriggerAsync(scope.ServiceProvider, "IH-OB-RES", "EQ-IH-RES");

        // RELOAD 후 전이 — Restore 경로라 이벤트 없음. 읽기경로가 PHANTOM 이벤트를 내면 COUNT>1로 드러난다.
        var history = (await repo.GetUnresolvedAsync("EQ-IH-RES")).Single();
        history.Resolve(DateTime.UtcNow);   // FdcInterlockResolvedDomainEvent 발행
        await repo.UpdateAsync(history);

        OutboxCount(On.ConnectionString, "EQ-IH-RES", "FdcInterlockResolved").Should().Be(1,
            "outbox 활성 시 인터락 해제와 같은 트랜잭션에 FdcInterlockResolved 이벤트가 1건 기록돼야 한다(읽기경로 PHANTOM 발행이면 >1)");
    }

    [Fact]
    public async Task Interlock_trigger_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        await TriggerAsync(scope.ServiceProvider, "IH-OB-OFF", "EQ-IH-OFF");

        var repo = scope.ServiceProvider.GetRequiredService<IFdcInterlockHistoryRepository>();
        var persisted = await repo.GetUnresolvedAsync("EQ-IH-OFF");
        persisted.Should().ContainSingle("outbox 비활성이어도 인터락 이력은 정상 기록돼야 한다");

        OutboxCount(Off.ConnectionString, "EQ-IH-OFF", "FdcInterlockTriggered").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
