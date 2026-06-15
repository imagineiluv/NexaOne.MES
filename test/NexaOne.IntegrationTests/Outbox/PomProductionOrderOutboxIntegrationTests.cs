using Microsoft.Extensions.DependencyInjection;
using NexaOne.POM.Application.Pom;
using NexaOne.POM.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — POM 슬라이스. 생산오더 전이(착수 등) 시 도메인 이벤트가 오더 행과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 전이당 1행, 비활성(기본)이면 0행. 핵심은 '리로드 후 전이' —
/// 영속 후 리포 getter로 다시 읽어(Restore 경로) 전이를 한 번만 적용해, ToDomain이 Create+재생을 쓰면 새어 나올
/// 유령 이벤트(phantom) 회귀를 잡는다. outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로
/// 확인한다. (테스트 SQLite는 FK off라 미등록 계획/설비 오더 INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증.)
/// </summary>
public sealed class PomProductionOrderOutboxIntegrationTests : OutboxIntegrationTestBase
{
    private static readonly DateTime Sched = new(2026, 3, 1, 8, 0, 0, DateTimeKind.Utc);

    public PomProductionOrderOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 발행 이벤트 없는 Issued 오더를 리포의 INSERT 경로로 영속한다(AddAsync는 이벤트를 발행하지 않는다).
    private static async Task IssueAsync(IServiceProvider sp, string orderId)
    {
        var repo = sp.GetRequiredService<IProductionOrderRepository>();
        var order = ProductionOrder.Create(orderId, "PLAN-OB", "EQ-OB", "PROD-OB", 100m, Sched, Sched.AddHours(10)).Value;
        await repo.AddAsync(order);
    }

    [Fact]
    public async Task Order_start_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductionOrderRepository>();
        await IssueAsync(scope.ServiceProvider, "PO-OB-ON");

        var order = await repo.GetByIdAsync("PO-OB-ON");   // Restore — 이벤트 없음
        order.Should().NotBeNull("오더가 영속돼야 한다(트랜잭션 커밋)");
        order!.Start(Sched.AddMinutes(5)).IsSuccess.Should().BeTrue();   // ProductionOrderStarted 발행
        await repo.UpdateAsync(order);

        OutboxCount(On.ConnectionString, "PO-OB-ON", "ProductionOrderStarted").Should().Be(1,
            "outbox 활성 시 착수와 같은 트랜잭션에 ProductionOrderStarted 이벤트가 1건 기록돼야 한다(리로드 후 전이 — 유령 이벤트 없음)");
    }

    [Fact]
    public async Task Order_start_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IProductionOrderRepository>();
        await IssueAsync(scope.ServiceProvider, "PO-OB-OFF");

        var order = await repo.GetByIdAsync("PO-OB-OFF");
        order!.Start(Sched.AddMinutes(5));
        await repo.UpdateAsync(order);

        var persisted = await repo.GetByIdAsync("PO-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 오더는 정상 기록·갱신돼야 한다");
        persisted!.Status.Should().Be(ProductionOrderStatus.InProgress, "전이는 데이터 행에 반영돼야 한다");

        OutboxCount(Off.ConnectionString, "PO-OB-OFF", "ProductionOrderStarted").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
