using Microsoft.Extensions.DependencyInjection;
using NexaOne.SHP.Application.Shp;
using NexaOne.SHP.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — 출하지시(DeliveryOrder) 애그리거트의 트랜잭션 outbox. 확정/출하/취소 시 도메인 이벤트가 주문 행과
/// '동일 트랜잭션'에 EES_OUTBOX로 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 전이 각 1행, 비활성(기본)이면
/// 0행. outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속 후 GetById로 다시
/// 로드해 전이→저장하므로(reload-then-mutate), 읽기경로가 Restore 아닌 Create+재생이면 팬텀 이벤트가 COUNT>1로 드러난다.
/// (테스트 SQLite는 FK off라 미등록 부모 없이 주문 INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class ShpDeliveryOrderOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public ShpDeliveryOrderOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 주문을 Draft로 영속한다(생성은 이벤트를 발행하지 않음 — 전이만 발행).
    private static async Task SeedDraftAsync(IServiceProvider sp, string orderId)
    {
        var repo = sp.GetRequiredService<IDeliveryOrderRepository>();
        var order = DeliveryOrder.Create(orderId, "Customer-OB", "P-OB", DateTime.UtcNow).Value;
        await repo.AddAsync(order);
    }

    [Fact]
    public async Task Confirm_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeliveryOrderRepository>();
        await SeedDraftAsync(scope.ServiceProvider, "DO-OB-CONF");

        var order = await repo.GetByIdAsync("DO-OB-CONF");   // Restore — 이벤트 없음
        order!.Confirm();                                     // DeliveryOrderConfirmedDomainEvent 발행
        await repo.UpdateAsync(order);

        OutboxCount(On.ConnectionString, "DO-OB-CONF", "DeliveryOrderConfirmed").Should().Be(1,
            "outbox 활성 시 확정과 같은 트랜잭션에 DeliveryOrderConfirmed 이벤트가 1건 기록돼야 한다(재로드 후 전이라 팬텀이면 >1)");
    }

    [Fact]
    public async Task Ship_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeliveryOrderRepository>();
        await SeedDraftAsync(scope.ServiceProvider, "DO-OB-SHIP");

        var confirmed = await repo.GetByIdAsync("DO-OB-SHIP");
        confirmed!.Confirm();
        await repo.UpdateAsync(confirmed);

        var order = await repo.GetByIdAsync("DO-OB-SHIP");   // Restore — Confirmed 상태로 복원
        order!.Ship(DateTime.UtcNow);                         // DeliveryOrderShippedDomainEvent 발행
        await repo.UpdateAsync(order);

        OutboxCount(On.ConnectionString, "DO-OB-SHIP", "DeliveryOrderShipped").Should().Be(1,
            "outbox 활성 시 출하와 같은 트랜잭션에 DeliveryOrderShipped 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Cancel_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeliveryOrderRepository>();
        await SeedDraftAsync(scope.ServiceProvider, "DO-OB-CANC");

        var order = await repo.GetByIdAsync("DO-OB-CANC");   // Restore — 이벤트 없음
        order!.Cancel();                                      // DeliveryOrderCancelledDomainEvent 발행
        await repo.UpdateAsync(order);

        OutboxCount(On.ConnectionString, "DO-OB-CANC", "DeliveryOrderCancelled").Should().Be(1,
            "outbox 활성 시 취소와 같은 트랜잭션에 DeliveryOrderCancelled 이벤트가 1건 기록돼야 한다");
    }

    [Fact]
    public async Task Confirm_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDeliveryOrderRepository>();
        await SeedDraftAsync(scope.ServiceProvider, "DO-OB-OFF");

        var order = await repo.GetByIdAsync("DO-OB-OFF");
        order!.Confirm();
        await repo.UpdateAsync(order);

        var persisted = await repo.GetByIdAsync("DO-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 주문은 정상 기록돼야 한다");
        persisted!.Status.Should().Be(DeliveryOrderStatus.Confirmed, "비활성 경로에서도 상태 전이는 영속돼야 한다");

        OutboxCount(Off.ConnectionString, "DO-OB-OFF", "DeliveryOrderConfirmed").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
