using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// EES_OUTBOX 영속(ADR-002) — ID 자동증가 INSERT가 SQLite에서 동작하는지 실증한다.
/// EES_OUTBOX.ID는 MSSQL BIGINT IDENTITY + 테이블레벨 PK인데, SQLite에서 자동증가하려면 부트스트랩이
/// 이를 컬럼레벨 INTEGER PRIMARY KEY(rowid 별칭)로 변환해야 한다 — 본 테스트가 그 전제를 가드한다.
/// </summary>
public sealed class OutboxPersistenceIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public OutboxPersistenceIntegrationTests(TestApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Enqueue_then_GetUnpublished_persists_row_on_sqlite()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();

        await repo.EnqueueAsync("EquipmentStateChanged", "EST", "EQ-OUTBOX-PROBE", "Running");

        var pending = await repo.GetUnpublishedAsync(50, 10);
        pending.Should().Contain(
            m => m.AggregateId == "EQ-OUTBOX-PROBE" && m.EventType == "EquipmentStateChanged",
            "ID 미지정 outbox INSERT가 SQLite에서 성공(자동증가)하고 미발행 조회로 되읽혀야 한다");
    }
}
