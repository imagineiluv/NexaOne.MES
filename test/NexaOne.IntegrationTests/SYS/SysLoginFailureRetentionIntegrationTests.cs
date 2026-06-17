using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.SYS.Application.Users;

namespace NexaOne.IntegrationTests.SYS;

/// <summary>
/// LoginFailureHistoryRepository.DeleteOlderThanAsync(cutoff) 보존정리를 SQLite에서 실증한다.
/// 삭제 술어 = OCCURRED_AT &lt; @cutoff (LoginFailureHistoryRepository.cs:34), 영향 행 수를 반환한다(동 :35).
/// SYS_LOGIN_FAILURE_HIST의 NOT NULL 컬럼(감사필드 포함)은 V011__SYS_LOGIN_FAILURE_HIST.sql:13-22를 빠짐없이 채운다.
/// OCCURRED_AT는 DATETIME2→TEXT(SqliteSchemaInitializer.cs:90)이라 @cutoff(DateTime 파라미터)와 동일 바인딩으로 시드한다.
/// </summary>
public sealed class SysLoginFailureRetentionIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public SysLoginFailureRetentionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // NOT NULL 컬럼(V011:13-22, 감사필드 CREATED_BY/AT·UPDATED_BY/AT 포함)을 채워 한 이력 행을 시드한다.
    private void SeedFailure(string failureId, DateTime occurredAt)
    {
        using var conn = new SqliteConnection(_factory.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO SYS_LOGIN_FAILURE_HIST
            (FAILURE_ID, USER_ID, IP_ADDRESS, USER_AGENT, FAILURE_REASON, OCCURRED_AT,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            ($id, $user, $ip, $agent, $reason, $at,
             $cby, $cat, $uby, $uat)";
        cmd.Parameters.AddWithValue("$id", failureId);
        cmd.Parameters.AddWithValue("$user", "USER-RET");
        cmd.Parameters.AddWithValue("$ip", "127.0.0.1");
        cmd.Parameters.AddWithValue("$agent", "integration-test");
        cmd.Parameters.AddWithValue("$reason", "InvalidPassword");
        cmd.Parameters.AddWithValue("$at", occurredAt);
        cmd.Parameters.AddWithValue("$cby", "TEST");
        cmd.Parameters.AddWithValue("$cat", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("$uby", "TEST");
        cmd.Parameters.AddWithValue("$uat", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    private bool Exists(string failureId)
    {
        using var conn = new SqliteConnection(_factory.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM SYS_LOGIN_FAILURE_HIST WHERE FAILURE_ID = $id";
        cmd.Parameters.AddWithValue("$id", failureId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public async Task DeleteOlderThanAsync_removes_only_rows_before_cutoff()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILoginFailureHistoryRepository>();

        // cutoff를 시드값 사이에 두어 타이밍 비의존. 고유 FAILURE_ID로만 단언(공유 테이블 병렬 간섭 방지).
        var cutoff = DateTime.UtcNow;
        const string old = "LF-RET-OLD-0001";    // cutoff 이전 → 삭제 대상
        const string recent = "LF-RET-NEW-0001";  // cutoff 이상 → 잔존

        SeedFailure(old, cutoff.AddHours(-1));
        SeedFailure(recent, cutoff.AddHours(+1));

        var deleted = await repo.DeleteOlderThanAsync(cutoff);

        deleted.Should().Be(1, "cutoff 이전 1행만 삭제돼 영향 행 수는 1이어야 한다");
        Exists(old).Should().BeFalse("cutoff 이전 행은 삭제돼야 한다");
        Exists(recent).Should().BeTrue("cutoff 이상 행은 잔존해야 한다(OCCURRED_AT < cutoff 미해당)");
    }
}
