using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.CMMS.Application.Cmms;
using NexaOne.CMMS.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — CMMS 작업지시 애그리거트. 작업지시 착수 시 도메인 이벤트가 작업지시 행과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 착수당 1행, 비활성(기본)이면 0행. outbox 행은 디스패처
/// 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속→GetById 재로딩(Restore)→착수→저장 순서라,
/// 읽기경로가 Create+replay로 회귀하면 팬텀 이벤트가 COUNT>1로 드러난다. (테스트 SQLite는 FK off라 미등록 설비
/// 작업지시 INSERT가 가능 — 부모를 시드하지 않는다.) AGGREGATE_ID는 작업지시별 순서 보장을 위해 WO_ID이다.
/// </summary>
public sealed class CmmsWorkOrderOutboxIntegrationTests
    : IClassFixture<TestApiFactory>, IClassFixture<OutboxEnabledTestApiFactory>
{
    private readonly TestApiFactory _off;
    private readonly OutboxEnabledTestApiFactory _on;

    public CmmsWorkOrderOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
    {
        _off = off;
        _on = on;
    }

    // 작업지시를 리포의 트랜잭션 경로로 영속한다(생성은 이벤트를 발행하지 않음 — 착수/완료/취소에서만 발행).
    private static async Task SeedAsync(IServiceProvider sp, string woId, string equipmentId)
    {
        var repo = sp.GetRequiredService<IWorkOrderRepository>();
        var wo = WorkOrder.Create(woId, equipmentId, "PM", "정기점검", "tech01", DateTime.UtcNow).Value;
        await repo.AddAsync(wo);
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
    public async Task WorkOrder_start_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = _on.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();
        await SeedAsync(scope.ServiceProvider, "WO-OB-ON", "EQ-WO-ON");

        var wo = await repo.GetByIdAsync("WO-OB-ON");   // Restore — 이벤트 없음(재로딩 후 착수해야 팬텀 이벤트가 드러난다)
        wo!.Start().IsSuccess.Should().BeTrue();          // WorkOrderStartedDomainEvent 발행
        await repo.UpdateAsync(wo);

        OutboxCount(_on.ConnectionString, "WO-OB-ON", "WorkOrderStarted").Should().Be(1,
            "outbox 활성 시 작업지시 착수와 같은 트랜잭션에 WorkOrderStarted 이벤트가 1건 기록돼야 한다(재로딩해도 팬텀 이벤트 없음)");
    }

    [Fact]
    public async Task WorkOrder_start_does_not_write_outbox_when_disabled()
    {
        using var scope = _off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IWorkOrderRepository>();
        await SeedAsync(scope.ServiceProvider, "WO-OB-OFF", "EQ-WO-OFF");

        var wo = await repo.GetByIdAsync("WO-OB-OFF");
        wo!.Start().IsSuccess.Should().BeTrue();
        await repo.UpdateAsync(wo);

        var persisted = await repo.GetByIdAsync("WO-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 작업지시는 정상 기록돼야 한다");
        persisted!.Status.Should().Be(WorkOrderStatus.InProgress, "착수 상태가 영속돼야 한다");

        OutboxCount(_off.ConnectionString, "WO-OB-OFF", "WorkOrderStarted").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
