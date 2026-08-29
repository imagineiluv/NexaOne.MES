using System.Globalization;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.EST.Domain;
using NexaOne.EST.Infrastructure;
using NexaOne.Infrastructure.Persistence;
using NexaDB.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>TEST-2 복원 — outbox 행 통합검증(ADR-002 대표 슬라이스, 폐기 IntegrationTests에서 소실됐던 커버리지).
/// 실제 EquipmentStateRepository를 호스트 부트 DB에 직접 구성해 (1)Events:Outbox:Enabled=true면 상태 전이가
/// 상태 업서트+이력+EES_OUTBOX 이벤트 행을 같은 트랜잭션으로 기록하고 (2)기본(off)이면 outbox 행이 생기지
/// 않음을 검증한다(발행 원자성 게이트).</summary>
public sealed class EstOutboxIntegrationTests : IClassFixture<EstOutboxIntegrationTests.OutboxFactory>
{
    private readonly OutboxFactory _factory;
    public EstOutboxIntegrationTests(OutboxFactory factory) => _factory = factory;

    public sealed class OutboxFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(Path.GetTempPath(), $"nexaone-outbox-{Guid.NewGuid():N}.db");
        public string ConnString => $"Data Source={DbPath};Foreign Keys=False";
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnString);
            builder.UseSetting("Jwt:SecretKey", "est-outbox-e2e-jwt-secret-key-32bytes+!!!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-outbox-test");
            builder.UseSetting("Jwt:Audience", "nexaone-outbox-test");
        }
        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* 임시 파일 정리 실패 무시 */ }
        }
    }

    private EquipmentStateRepository BuildRepo(bool outboxEnabled)
    {
        _ = _factory.CreateClient(); // 스키마 부트스트랩
        var ds = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnString,
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Events:Outbox:Enabled"] = outboxEnabled ? "true" : "false",
        }).Build();
        return new EquipmentStateRepository(ds, new SqliteEesDbCapability(), config);
    }

    private T Scalar<T>(string sql, params (string Key, object Value)[] ps)
    {
        using var conn = new SqliteConnection(_factory.ConnString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private static (EquipmentCurrentState State, EquipmentStateHistory History) Transition(string equipmentId)
    {
        var state = EquipmentCurrentState.Create(equipmentId, "PLANT01");
        state.ApplyTransition("RUN");   // ADR-002 — EquipmentStateChangedDomainEvent 발행
        var history = EquipmentStateHistory.Create(
            $"{equipmentId}_{Guid.NewGuid():N}"[..40], equipmentId, "IDLE", "RUN", "RUN",
            DateTime.UtcNow, "outbox-tester").Value;
        return (state, history);
    }

    [Fact]
    public async Task Outbox_enabled_state_change_writes_event_row_in_same_transaction()
    {
        var eq = $"EQOB_{Guid.NewGuid():N}"[..20];
        var (state, history) = Transition(eq);
        var repo = BuildRepo(outboxEnabled: true);

        (await repo.TryInitializeAsync(EquipmentCurrentState.Create(eq, "PLANT01"))).Should().BeTrue();
        (await repo.TryChangeStateWithHistoryAsync(state, history, expectedVersion: 1)).Should().BeTrue();

        Scalar<long>("SELECT COUNT(*) FROM EST_EQUIPMENT_STATE WHERE EQUIPMENT_ID=@eq", ("@eq", eq)).Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM EST_EQUIPMENT_STATE_HISTORY WHERE EQUIPMENT_ID=@eq", ("@eq", eq)).Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID=@eq AND EVENT_TYPE LIKE '%EquipmentStateChanged%' AND PUBLISHED_AT IS NULL",
            ("@eq", eq)).Should().Be(1, "outbox 활성 시 상태·이력·이벤트가 같은 트랜잭션으로 기록돼야 한다(ADR-002 발행 원자성)");
        state.DomainEvents.Should().BeEmpty("영속 후 도메인 이벤트는 비워져 재발행을 막는다");
    }

    [Fact]
    public async Task Outbox_disabled_state_change_writes_no_event_row()
    {
        var eq = $"EQOB_{Guid.NewGuid():N}"[..20];
        var (state, history) = Transition(eq);
        var repo = BuildRepo(outboxEnabled: false);

        (await repo.TryInitializeAsync(EquipmentCurrentState.Create(eq, "PLANT01"))).Should().BeTrue();
        (await repo.TryChangeStateWithHistoryAsync(state, history, expectedVersion: 1)).Should().BeTrue();

        Scalar<long>("SELECT COUNT(*) FROM EST_EQUIPMENT_STATE WHERE EQUIPMENT_ID=@eq", ("@eq", eq)).Should().Be(1);
        Scalar<long>("SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID=@eq", ("@eq", eq)).Should().Be(0,
            "기본(off)에서는 outbox 행이 생기지 않아야 한다(게이트 검증)");
    }

    [Fact]
    public async Task State_version_compare_and_swap_allows_only_one_transition_history()
    {
        var eq = $"EQCAS_{Guid.NewGuid():N}"[..20];
        var repo = BuildRepo(outboxEnabled: false);
        (await repo.TryInitializeAsync(EquipmentCurrentState.Create(eq, "PLANT01"))).Should().BeTrue();

        var first = EquipmentCurrentState.Restore(eq, "PLANT01", "IDLE", DateTime.UtcNow, 1);
        var second = EquipmentCurrentState.Restore(eq, "PLANT01", "IDLE", DateTime.UtcNow, 1);
        first.ApplyTransition("RUN");
        second.ApplyTransition("DOWN");
        var firstHistory = EquipmentStateHistory.Create(
            $"H1_{Guid.NewGuid():N}", eq, "IDLE", "RUN", "RUN", DateTime.UtcNow, "user-1").Value;
        var secondHistory = EquipmentStateHistory.Create(
            $"H2_{Guid.NewGuid():N}", eq, "IDLE", "DOWN", "DOWN", DateTime.UtcNow, "user-2").Value;

        (await repo.TryChangeStateWithHistoryAsync(first, firstHistory, 1)).Should().BeTrue();
        (await repo.TryChangeStateWithHistoryAsync(second, secondHistory, 1)).Should().BeFalse();

        var persisted = await repo.GetAsync(eq);
        persisted!.CurrentStateId.Should().Be("RUN");
        persisted.StateVersion.Should().Be(2);
        Scalar<long>("SELECT COUNT(*) FROM EST_EQUIPMENT_STATE_HISTORY WHERE EQUIPMENT_ID=@eq", ("@eq", eq))
            .Should().Be(1, "the losing CAS must not append a history row");
    }
}
