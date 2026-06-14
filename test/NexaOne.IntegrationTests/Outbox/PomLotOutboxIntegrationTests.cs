using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.POM.Application.Lots;
using NexaOne.POM.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 POM 슬라이스 — Lot 전이(TrackIn 등) 시 도메인 이벤트가 Lot 행과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 전이당 1행, 비활성(기본)이면 0행. 영속 후 리포의
/// GetByIdAsync로 '다시 로드'한 Lot에 전이를 적용·저장해, Restore 읽기경로가 팬텀 이벤트를 만들지 않음을
/// 함께 검증한다(reload-then-mutate). outbox 행은 디스패처 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로
/// 확인한다. (테스트 SQLite는 FK off라 미등록 부모 없이 Lot INSERT가 가능 — 여기서는 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class PomLotOutboxIntegrationTests
    : IClassFixture<TestApiFactory>, IClassFixture<OutboxEnabledTestApiFactory>
{
    private readonly TestApiFactory _off;
    private readonly OutboxEnabledTestApiFactory _on;

    public PomLotOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
    {
        _off = off;
        _on = on;
    }

    // Queued Lot을 리포에 영속한다(Create는 Created->Queued까지 — 아직 이벤트 발행 전).
    private static async Task SeedQueuedLotAsync(IServiceProvider sp, string lotId)
    {
        var repo = sp.GetRequiredService<ILotRepository>();
        var lot = Lot.Create(lotId, "P1", "WO-1", "PROD-1", 100m, ["CUT", "ASSY"], "seeder").Value;
        await repo.AddAsync(lot);
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
    public async Task Lot_trackin_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = _on.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILotRepository>();
        await SeedQueuedLotAsync(scope.ServiceProvider, "LOT-OB-ON");

        var lot = await repo.GetByIdAsync("LOT-OB-ON");   // Restore — 이벤트 없음(팬텀 회귀 탐지)
        lot!.TrackIn("EQ-ON", "RCP-1", 1, "worker", DateTime.UtcNow).IsSuccess.Should().BeTrue();
        await repo.UpdateAsync(lot);                        // LotTrackedInDomainEvent 발행

        OutboxCount(_on.ConnectionString, "LOT-OB-ON", "LotTrackedIn").Should().Be(1,
            "outbox 활성 시 TrackIn과 같은 트랜잭션에 LotTrackedIn 이벤트가 1건만 기록돼야 한다(재로드 후 전이라 팬텀이 없어야 함)");
    }

    [Fact]
    public async Task Lot_trackin_does_not_write_outbox_when_disabled()
    {
        using var scope = _off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILotRepository>();
        await SeedQueuedLotAsync(scope.ServiceProvider, "LOT-OB-OFF");

        var lot = await repo.GetByIdAsync("LOT-OB-OFF");
        lot!.TrackIn("EQ-OFF", "RCP-1", 1, "worker", DateTime.UtcNow).IsSuccess.Should().BeTrue();
        await repo.UpdateAsync(lot);

        var persisted = await repo.GetByIdAsync("LOT-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 Lot 전이는 정상 기록돼야 한다");
        persisted!.State.Should().Be(LotState.Processing, "TrackIn 전이가 데이터 행에 반영돼야 한다");

        OutboxCount(_off.ConnectionString, "LOT-OB-OFF", "LotTrackedIn").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
