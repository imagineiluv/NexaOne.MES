using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.FDC.Application.Fdc;

namespace NexaOne.IntegrationTests.Fdc;

/// <summary>
/// FdcCollectDataRepository.DeleteOlderThanAsync(cutoff) 보존정리를 SQLite에서 실증한다.
/// 삭제 술어 = COLLECTED_AT &lt; @cutoff (FdcCollectDataRepository.cs:59), 영향 행 수를 반환한다(동 :60).
/// FDC_COLLECT_DATA는 감사 컬럼 없는 시계열 테이블 — 8개 도메인 컬럼만 NOT NULL(V017__FDC_COLLECT_DATA.sql:5-12).
/// COLLECTED_AT는 DATETIME2→TEXT(SqliteSchemaInitializer.cs:90)이라 @cutoff(DateTime 파라미터)와 동일 바인딩으로 시드한다.
/// </summary>
public sealed class FdcCollectDataRetentionIntegrationTests : IClassFixture<TestApiFactory>
{
    private readonly TestApiFactory _factory;

    public FdcCollectDataRetentionIntegrationTests(TestApiFactory factory) => _factory = factory;

    // NOT NULL 8컬럼(V017:5-12)을 채워 시계열 한 행을 시드한다(감사 컬럼 없음). COLLECTED_AT는 DateTime 파라미터로 바인딩.
    private void SeedData(string collectId, DateTime collectedAt)
    {
        using var conn = new SqliteConnection(_factory.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO FDC_COLLECT_DATA
            (COLLECT_ID, EQUIPMENT_ID, PARAMETER_ID, VALUE, COLLECTED_AT, QUALITY, LOWER_LIMIT, UPPER_LIMIT)
            VALUES
            ($id, $equip, $param, $value, $at, $quality, $lower, $upper)";
        cmd.Parameters.AddWithValue("$id", collectId);
        cmd.Parameters.AddWithValue("$equip", "EQ-RET");
        cmd.Parameters.AddWithValue("$param", "PARAM-RET");
        cmd.Parameters.AddWithValue("$value", 12.5m);
        cmd.Parameters.AddWithValue("$at", collectedAt);
        cmd.Parameters.AddWithValue("$quality", "Good");
        cmd.Parameters.AddWithValue("$lower", 0.0m);
        cmd.Parameters.AddWithValue("$upper", 100.0m);
        cmd.ExecuteNonQuery();
    }

    private bool Exists(string collectId)
    {
        using var conn = new SqliteConnection(_factory.ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM FDC_COLLECT_DATA WHERE COLLECT_ID = $id";
        cmd.Parameters.AddWithValue("$id", collectId);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    [Fact]
    public async Task DeleteOlderThanAsync_removes_only_rows_before_cutoff()
    {
        using var scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<IFdcCollectDataRepository>();

        // cutoff를 시드값 사이에 두어 타이밍 비의존(명확한 과거/미래 오프셋). 고유 COLLECT_ID로만 단언(공유 테이블).
        var cutoff = DateTime.UtcNow;
        const string old = "RET-OLD-0001";    // cutoff 이전 → 삭제 대상
        const string recent = "RET-NEW-0001";  // cutoff 이상 → 잔존

        SeedData(old, cutoff.AddHours(-1));
        SeedData(recent, cutoff.AddHours(+1));

        var deleted = await repo.DeleteOlderThanAsync(cutoff);

        deleted.Should().Be(1, "cutoff 이전 1행만 삭제돼 영향 행 수는 1이어야 한다");
        Exists(old).Should().BeFalse("cutoff 이전 행은 삭제돼야 한다");
        Exists(recent).Should().BeTrue("cutoff 이상 행은 잔존해야 한다(COLLECTED_AT < cutoff 미해당)");
    }
}
