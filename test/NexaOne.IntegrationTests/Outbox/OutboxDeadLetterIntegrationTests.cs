using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 outbox 운영 강화 — 데드레터 전이·지수 백오프 게이트·재시도 리셋을 SQLite에서 실증한다.
/// V033 마이그레이션이 추가한 DEAD_LETTERED_AT/NEXT_ATTEMPT_AT 두 컬럼이 테스트 스키마에 존재함을 전제로 하며
/// (SqliteSchemaBootstrapper의 ALTER→ADD COLUMN 변환), GetUnpublished 폴링 술어가 두 컬럼을 반영하는지 검증한다.
/// </summary>
public sealed class OutboxDeadLetterIntegrationTests
    : IClassFixture<OutboxEnabledTestApiFactory>
{
    private const int MaxAttempts = 3;
    private readonly OutboxEnabledTestApiFactory _factory;

    public OutboxDeadLetterIntegrationTests(OutboxEnabledTestApiFactory factory) => _factory = factory;

    // 미발행 폴링 결과에서 특정 aggregateId가 보이는지(결정론적) — 디스패처 없이 리포 직접 호출.
    private static async Task<bool> IsPollable(IOutboxRepository repo, string aggregateId)
    {
        var pending = await repo.GetUnpublishedAsync(100, MaxAttempts);
        return pending.Any(m => m.AggregateId == aggregateId);
    }

    // NEXT_ATTEMPT_AT를 과거로 당겨 백오프 창을 강제로 연다(타이밍 의존 없이 재폴링 가능 시점을 시뮬레이션).
    // Microsoft.Data.Sqlite의 datetime() 출력은 GetUnpublished가 비교하는 ISO-8601 TEXT와 동일 정렬형식이다.
    private static void OpenBackoffWindow(string connectionString, string aggregateId)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "UPDATE EES_OUTBOX SET NEXT_ATTEMPT_AT = datetime('now','-1 hour') WHERE AGGREGATE_ID = $id";
        cmd.Parameters.AddWithValue("$id", aggregateId);
        cmd.ExecuteNonQuery();
    }

    private static long FindId(string connectionString, string aggregateId)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ID FROM EES_OUTBOX WHERE AGGREGATE_ID = $id";
        cmd.Parameters.AddWithValue("$id", aggregateId);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    [Fact]
    public async Task Message_failing_max_attempts_is_dead_lettered_and_excluded_from_poll()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        const string agg = "DLQ-DEADLETTER";

        await repo.EnqueueAsync("EquipmentStateChanged", "EST", agg, "Running");
        var id = FindId(_factory.ConnectionString, agg);

        // maxAttempts번 실패시킨다. attempts는 현재값(증가 전)으로 0,1,...,max-1을 순차 전달한다(디스패처와 동일).
        for (var attempts = 0; attempts < MaxAttempts; attempts++)
            await repo.MarkFailedAsync(id, attempts, MaxAttempts);

        // 한계 도달 → DEAD_LETTERED_AT가 설정되어 폴링에서 영구 제외된다.
        (await IsPollable(repo, agg)).Should().BeFalse(
            "maxAttempts 도달 시 DEAD_LETTERED_AT가 설정되어 GetUnpublished에서 영구 제외돼야 한다");

        var deadLettered = await repo.GetDeadLetteredAsync(100);
        deadLettered.Should().Contain(m => m.AggregateId == agg && m.DeadLetteredAt != null,
            "한계 도달 메시지는 GetDeadLettered로 조회되고 DEAD_LETTERED_AT가 채워져야 한다");
        (await repo.CountDeadLetteredAsync()).Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Freshly_failed_message_is_gated_until_next_attempt_at()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        const string agg = "DLQ-BACKOFF";

        await repo.EnqueueAsync("EquipmentStateChanged", "EST", agg, "Running");
        var id = FindId(_factory.ConnectionString, agg);

        (await IsPollable(repo, agg)).Should().BeTrue("초기(미실패) 메시지는 즉시 폴링 가능해야 한다");

        // 1회 실패 → NEXT_ATTEMPT_AT가 미래(now + 2^0초)로 설정되어 즉시 재폴링에서 제외된다(백오프 게이트).
        await repo.MarkFailedAsync(id, attempts: 0, maxAttempts: MaxAttempts);
        (await IsPollable(repo, agg)).Should().BeFalse(
            "방금 실패한 메시지는 NEXT_ATTEMPT_AT(백오프) 이전에는 재폴링되지 않아야 한다");

        // 백오프 창이 지나면(NEXT_ATTEMPT_AT <= now) 다시 폴링 가능해진다.
        OpenBackoffWindow(_factory.ConnectionString, agg);
        (await IsPollable(repo, agg)).Should().BeTrue(
            "NEXT_ATTEMPT_AT가 경과하면 메시지는 다시 폴링 대상이 돼야 한다");
    }

    [Fact]
    public async Task ResetForRetry_makes_dead_lettered_message_pollable_again()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IOutboxRepository>();
        const string agg = "DLQ-RESET";

        await repo.EnqueueAsync("EquipmentStateChanged", "EST", agg, "Running");
        var id = FindId(_factory.ConnectionString, agg);

        for (var attempts = 0; attempts < MaxAttempts; attempts++)
            await repo.MarkFailedAsync(id, attempts, MaxAttempts);
        (await IsPollable(repo, agg)).Should().BeFalse("데드레터 상태에서는 폴링되지 않아야 한다(전제 확인)");

        // 재시도 리셋: DEAD_LETTERED_AT·NEXT_ATTEMPT_AT 해제 + ATTEMPTS=0 → 다시 폴링 가능.
        await repo.ResetForRetryAsync(id);
        (await IsPollable(repo, agg)).Should().BeTrue(
            "ResetForRetry 후 DEAD_LETTERED_AT·NEXT_ATTEMPT_AT가 해제되고 ATTEMPTS=0이라 다시 폴링돼야 한다");
    }
}
