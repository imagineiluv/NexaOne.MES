using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — QMS 부적합 애그리거트. 부적합 확정 시 도메인 이벤트가 부적합 행과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 확정당 1행, 비활성(기본)이면 0행. outbox 행은 디스패처
/// 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속→GetById 재로딩(Restore)→확정→저장 순서라,
/// 읽기경로가 Create+replay로 회귀하면 팬텀 이벤트가 COUNT>1로 드러난다. (테스트 SQLite는 FK off라 미등록 Lot/설비
/// 부적합 INSERT가 가능 — 부모를 시드하지 않는다.)
/// </summary>
public sealed class QmsDefectOutboxIntegrationTests
    : IClassFixture<TestApiFactory>, IClassFixture<OutboxEnabledTestApiFactory>
{
    private readonly TestApiFactory _off;
    private readonly OutboxEnabledTestApiFactory _on;

    public QmsDefectOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
    {
        _off = off;
        _on = on;
    }

    // 부적합을 리포의 트랜잭션 경로로 영속한다(생성은 이벤트를 발행하지 않음 — 확정에서만 발행).
    private static async Task SeedAsync(IServiceProvider sp, string defectId, string lotId)
    {
        var repo = sp.GetRequiredService<IDefectRepository>();
        var defect = Defect.Create(defectId, lotId, "EQ-DF", "DC-SCRATCH", 7, 0.035m, DateTime.UtcNow, "INS-1").Value;
        await repo.AddAsync(defect);
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
    public async Task Defect_confirm_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = _on.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDefectRepository>();
        await SeedAsync(scope.ServiceProvider, "DF-OB-ON", "LOT-DF-ON");

        var defect = await repo.GetByIdAsync("DF-OB-ON");   // Restore — 이벤트 없음(재로딩 후 확정해야 팬텀 이벤트가 드러난다)
        defect!.Confirm("QA-9").IsSuccess.Should().BeTrue();  // DefectConfirmedDomainEvent 발행
        await repo.UpdateAsync(defect);

        OutboxCount(_on.ConnectionString, "LOT-DF-ON", "DefectConfirmed").Should().Be(1,
            "outbox 활성 시 부적합 확정과 같은 트랜잭션에 DefectConfirmed 이벤트가 1건 기록돼야 한다(재로딩해도 팬텀 이벤트 없음)");
    }

    [Fact]
    public async Task Defect_confirm_does_not_write_outbox_when_disabled()
    {
        using var scope = _off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IDefectRepository>();
        await SeedAsync(scope.ServiceProvider, "DF-OB-OFF", "LOT-DF-OFF");

        var defect = await repo.GetByIdAsync("DF-OB-OFF");
        defect!.Confirm("QA-9").IsSuccess.Should().BeTrue();
        await repo.UpdateAsync(defect);

        var persisted = await repo.GetByIdAsync("DF-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 부적합은 정상 기록돼야 한다");
        persisted!.IsConfirmed.Should().BeTrue("확정 상태가 영속돼야 한다");

        OutboxCount(_off.ConnectionString, "LOT-DF-OFF", "DefectConfirmed").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
