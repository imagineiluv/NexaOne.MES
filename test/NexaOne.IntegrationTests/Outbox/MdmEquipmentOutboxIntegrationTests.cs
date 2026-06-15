using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.MDM.Application.Equipments;
using NexaOne.MDM.Domain;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 — MDM 설비 비활성화/활성화/상위변경 시 도메인 이벤트가 설비 행과 '동일 트랜잭션'에 EES_OUTBOX로
/// 기록되는지(opt-in)를 SQLite에서 검증한다. 활성이면 전이당 1행, 비활성(기본)이면 0행. outbox 행은 디스패처
/// 발행 여부와 무관하게 DB를 직접 조회해 결정론적으로 확인한다. 영속 후 리포 getter로 '다시 읽어' 전이를
/// 적용하므로, 읽기경로가 이벤트를 재발행하면(팬텀) COUNT가 2가 되어 회귀를 잡는다. (테스트 SQLite는 FK off라
/// 미등록 상위 설비 INSERT가 가능 — 부모 시드 없이 outbox 트랜잭션 경로만 검증한다.)
/// </summary>
public sealed class MdmEquipmentOutboxIntegrationTests : OutboxIntegrationTestBase
{
    public MdmEquipmentOutboxIntegrationTests(TestApiFactory off, OutboxEnabledTestApiFactory on)
        : base(off, on)
    {
    }

    // 설비를 리포로 영속한다(AddAsync는 이벤트를 발행하지 않으므로 outbox 행이 생기지 않는다 — 전이만 발행 대상).
    private static async Task AddAsync(IServiceProvider sp, string equipmentId)
    {
        var repo = sp.GetRequiredService<IEquipmentRepository>();
        var eq = Equipment.Create(equipmentId, "Furnace #1", "P01", "A01", "Furnace").Value;
        await repo.AddAsync(eq);
    }

    [Fact]
    public async Task Deactivate_writes_outbox_in_same_transaction_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEquipmentRepository>();
        await AddAsync(scope.ServiceProvider, "EQ-OB-ON");

        var eq = await repo.GetByIdAsync("EQ-OB-ON");   // Restore — 이벤트 없음(팬텀 회귀 검증)
        eq!.Deactivate();                                // EquipmentDeactivatedDomainEvent 발행
        await repo.UpdateAsync(eq);

        OutboxCount(On.ConnectionString, "EQ-OB-ON", "EquipmentDeactivated").Should().Be(1,
            "outbox 활성 시 비활성화와 같은 트랜잭션에 EquipmentDeactivated 이벤트가 정확히 1건 기록돼야 한다");
    }

    [Fact]
    public async Task UpdateInfo_writes_no_outbox_even_when_enabled()
    {
        using var scope = On.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEquipmentRepository>();
        await AddAsync(scope.ServiceProvider, "EQ-OB-NOEV");

        var eq = await repo.GetByIdAsync("EQ-OB-NOEV");
        eq!.UpdateInfo("Renamed", "desc", "Furnace", "V", "M");   // 비이벤트 편집 — DomainEvents 비어 있음
        await repo.UpdateAsync(eq);

        var persisted = await repo.GetByIdAsync("EQ-OB-NOEV");
        persisted!.EquipmentName.Should().Be("Renamed", "비이벤트 편집도 데이터 행은 정상 갱신돼야 한다");

        using var conn = new SqliteConnection(On.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID = $id";
        cmd.Parameters.AddWithValue("$id", "EQ-OB-NOEV");
        Convert.ToInt32(cmd.ExecuteScalar()).Should().Be(0,
            "이벤트를 발행하지 않는 편집은 outbox INSERT가 0건이어야 한다(게이트 경로의 빈 이벤트 처리)");
    }

    [Fact]
    public async Task Deactivate_does_not_write_outbox_when_disabled()
    {
        using var scope = Off.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IEquipmentRepository>();
        await AddAsync(scope.ServiceProvider, "EQ-OB-OFF");

        var eq = await repo.GetByIdAsync("EQ-OB-OFF");
        eq!.Deactivate();
        await repo.UpdateAsync(eq);

        var persisted = await repo.GetByIdAsync("EQ-OB-OFF");
        persisted.Should().NotBeNull("outbox 비활성이어도 설비는 정상 기록돼야 한다");
        persisted!.ValidState.Should().Be("Invalid", "비활성 경로에서도 데이터 전이는 영속돼야 한다");

        OutboxCount(Off.ConnectionString, "EQ-OB-OFF", "EquipmentDeactivated").Should().Be(0,
            "outbox 비활성(기본)에서는 outbox 행을 기록하지 않아야 한다(적체 없음)");
    }
}
