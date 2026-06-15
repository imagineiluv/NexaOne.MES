using Microsoft.Extensions.DependencyInjection;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — CMMS 정비계획 슬라이스. 착수/완료/취소 시 도메인 이벤트가 계획 행과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 전이당 1행, 비활성(기본)이면 0행. outbox 행은 디스패처
/// 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속→리포 getter로 RELOAD 후 1전이만 적용해
/// 저장하므로, 읽기경로(Restore)가 phantom 이벤트를 발행하면 COUNT가 부풀어 회귀가 잡힌다. (테스트 SQLite는
/// FK off라 미등록 설비 계획 INSERT 가능 — 부모 시드 없이 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class CmmsMaintenancePlanOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public CmmsMaintenancePlanOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 정비계획을 리포의 트랜잭션 경로로 영속한다(Planned — 아직 이벤트 없음).
    private static async Task AddPlannedAsync(IServiceProvider sp, string planId, string equipmentId)
    {
        var repo = sp.GetRequiredService<IMaintenancePlanRepository>();
        var plan = MaintenancePlan.Create(planId, "월간 점검", equipmentId, "PM", "Monthly",
            DateTime.UtcNow, 2.5m, "USR-1").Value;
        await repo.AddAsync(plan);
    }

    [Fact]
    public async Task Plan_start_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>();
        await AddPlannedAsync(scope.ServiceProvider, "PL-OB-ON", "EQ-PL-ON");

        var plan = await repo.GetByIdAsync("PL-OB-ON");   // Restore — 이벤트 없음(phantom 없음 검증 포함)
        plan!.Start().IsSuccess.Should().BeTrue();          // MaintenancePlanStarted 발행
        await repo.UpdateAsync(plan);

        OutboxCount(On.ConnectionString, "PL-OB-ON", "MaintenancePlanStarted").Should().Be(1,
            "outbox 활성 시 착수와 같은 트랜잭션에 MaintenancePlanStarted 이벤트가 1건 기록돼야 한다(읽기경로 phantom 없음)");
    }

    [Fact]
    public async Task Plan_start_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IMaintenancePlanRepository>();
        await AddPlannedAsync(scope.ServiceProvider, "PL-OB-OFF", "EQ-PL-OFF");

        var plan = await repo.GetByIdAsync("PL-OB-OFF");
        plan!.Start().IsSuccess.Should().BeTrue();
        await repo.UpdateAsync(plan);

        var persisted = await repo.GetByIdAsync("PL-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 계획 상태 전이는 정상 기록돼야 한다");
        persisted!.Status.Should().Be(MaintenancePlanStatus.InProgress);

        OutboxCount(Off.ConnectionString, "PL-OB-OFF", "MaintenancePlanStarted").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
