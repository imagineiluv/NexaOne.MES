using Microsoft.Data.Sqlite;

namespace NexaOne.IntegrationTests.Outbox;

/// <summary>
/// ADR-002 outbox 통합 테스트 공통 베이스. 두 팩토리(기본 비활성 <see cref="TestApiFactory"/> +
/// opt-in 활성 <see cref="OutboxEnabledTestApiFactory"/>)를 클래스 픽스처로 주입받고, EES_OUTBOX를 직접
/// 조회하는 결정론적 헬퍼를 제공한다(디스패처 발행 여부와 무관하게 DB를 직접 COUNT). 파생 테스트는 중복된
/// 팩토리 생성자·필드·OutboxCount 헬퍼 없이 <see cref="On"/>/<see cref="Off"/>와 헬퍼만 사용한다.
/// </summary>
public abstract class OutboxIntegrationTestBase
    : IClassFixture<TestApiFactory>, IClassFixture<OutboxEnabledTestApiFactory>
{
    /// <summary>outbox 비활성(기본) 팩토리 — 0행(적체 없음) 검증용.</summary>
    protected TestApiFactory Off { get; }

    /// <summary>outbox 활성(opt-in) 팩토리 — 동일 트랜잭션 outbox 기록 검증용.</summary>
    protected OutboxEnabledTestApiFactory On { get; }

    protected OutboxIntegrationTestBase(TestApiFactory off, OutboxEnabledTestApiFactory on)
    {
        Off = off;
        On = on;
    }

    // EES_OUTBOX를 직접 조회(발행/미발행 무관) — 디스패처 타이밍에 의존하지 않는 결정론적 검증.
    protected static int OutboxCount(string connectionString, string aggregateId, string eventType)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID = $id AND EVENT_TYPE = $type";
        cmd.Parameters.AddWithValue("$id", aggregateId);
        cmd.Parameters.AddWithValue("$type", eventType);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    // EquipmentStateChanged 고정 조회 — EST 상태 슬라이스가 쓰는 2-인자 오버로드.
    protected static int OutboxCount(string connectionString, string aggregateId)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM EES_OUTBOX WHERE AGGREGATE_ID = $id AND EVENT_TYPE = 'EquipmentStateChanged'";
        cmd.Parameters.AddWithValue("$id", aggregateId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }
}
