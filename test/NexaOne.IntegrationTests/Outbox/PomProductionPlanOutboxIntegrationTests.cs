using Microsoft.Extensions.DependencyInjection;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — POM 생산계획 슬라이스. 상태 전이(Release/Start/Complete/Cancel) 시 도메인 이벤트가 계획 행과 '동일
/// 트랜잭션'에 EES_OUTBOX로 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 1행, 비활성(기본)이면 0행.
/// 검증은 '저장→리포 GetById로 재로드→전이→저장' 순서로 한다 — 읽기경로(Restore) 회귀로 팬텀 이벤트가 생기면
/// COUNT>1로 드러난다. outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다.
/// (테스트 SQLite는 FK off라 미등록 부모 없이 INSERT 가능 — 부모를 시드하지 않는다.)
/// </summary>
public sealed class PomProductionPlanOutboxIntegrationTests : OutboxIntegrationTestBase
{
    private const string EventType = "ProductionPlanStatusChanged";

    public PomProductionPlanOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // Draft 계획을 리포로 영속한다(AddAsync는 이벤트를 발행하지 않는다 — 생성은 전이가 아니다).
    private static async Task AddDraftAsync(IServiceProvider sp, string planId)
    {
        var repo = sp.GetRequiredService<IProductionPlanRepository>();
        var plan = ProductionPlan.Create(planId, "통합 계획", "PLANT-1", "PRD-1", 100m,
            new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 2, 0, 0, 0, DateTimeKind.Utc)).Value;
        await repo.AddAsync(plan);
    }

    [Fact]
    public async Task StatusChange_writes_outbox_in_same_transaction_when_enabled()
    {
        const string planId = "PP-OB-ON";
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductionPlanRepository>();
        await AddDraftAsync(scope.ServiceProvider, planId);

        var plan = await repo.GetByIdAsync(planId);   // Restore — 이벤트 없음(팬텀 회귀 시 여기서 이벤트가 생긴다)
        plan.Should().NotBeNull("Draft 계획이 영속돼야 한다");
        plan!.Release().IsSuccess.Should().BeTrue();  // ProductionPlanStatusChanged 발행
        await repo.UpdateAsync(plan);

        var reloaded = await repo.GetByIdAsync(planId);
        reloaded!.Status.Should().Be(ProductionPlanStatus.Released, "상태 전이가 영속돼야 한다(트랜잭션 커밋)");

        OutboxCount(On.ConnectionString, planId, EventType).Should().Be(1,
            "outbox 활성 시 상태 전이와 같은 트랜잭션에 ProductionPlanStatusChanged 이벤트가 정확히 1건 기록돼야 한다(재로드-후-전이라 팬텀이면 >1)");
    }

    [Fact]
    public async Task StatusChange_does_not_write_outbox_when_disabled()
    {
        const string planId = "PP-OB-OFF";
        using var scope = Off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductionPlanRepository>();
        await AddDraftAsync(scope.ServiceProvider, planId);

        var plan = await repo.GetByIdAsync(planId);
        plan!.Release().IsSuccess.Should().BeTrue();
        await repo.UpdateAsync(plan);

        var reloaded = await repo.GetByIdAsync(planId);
        reloaded!.Status.Should().Be(ProductionPlanStatus.Released, "outbox 비활성이어도 상태 전이는 정상 기록돼야 한다");

        OutboxCount(Off.ConnectionString, planId, EventType).Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
