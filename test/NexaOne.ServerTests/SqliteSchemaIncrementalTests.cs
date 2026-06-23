using FluentAssertions;
using Microsoft.Data.Sqlite;
using NexaOne.Infrastructure.Persistence;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// SqliteSchemaInitializer 증분 생성 경로 — 기존(빈 DB가 아닌) DB에 신규 마이그레이션 테이블이 누락돼도
/// EnsureSchema가 재기동 시 자동 생성한다(과거엔 테이블이 하나라도 있으면 전부 건너뛰어 신규 테이블이 영영 생성 안 됨).
/// 동시에 1회성 시드(INSERT)는 증분 패스에서 재실행하지 않아 시드 행이 중복되지 않음을 보장한다.
/// </summary>
public sealed class SqliteSchemaIncrementalTests
{
    private static string NewDb()
        => $"Data Source={Path.Combine(Path.GetTempPath(), $"nexa-incr-{Guid.NewGuid():N}.db")};Foreign Keys=False";

    private static string FileOf(string cs) => cs.Replace("Data Source=", "").Split(';')[0];

    private static bool TableExists(string cs, string name)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=@n";
        cmd.Parameters.AddWithValue("@n", name);
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L) > 0;
    }

    private static long Count(string cs, string table)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table}";
        return Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
    }

    private static void ExecSql(string cs, string sql)
    {
        using var c = new SqliteConnection(cs); c.Open();
        using var cmd = c.CreateCommand(); cmd.CommandText = sql; cmd.ExecuteNonQuery();
    }

    [Fact]
    public void EnsureSchema_ExistingDbMissingTable_AutoCreatesIt()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);                 // 전체 스키마 1회 적용(현 코드 기준 전 테이블)
            TableExists(cs, "MDM_BOM").Should().BeTrue();
            ExecSql(cs, "DROP TABLE MDM_BOM;");                 // 신규 마이그레이션 테이블이 아직 없는 구 DB 모사
            TableExists(cs, "MDM_BOM").Should().BeFalse();

            SqliteSchemaInitializer.EnsureSchema(cs);          // 기존 DB → 증분 생성 경로

            TableExists(cs, "MDM_BOM").Should().BeTrue();      // 누락 테이블이 자동 생성됨
            TableExists(cs, "MDM_QTIME_ACTION").Should().BeTrue();
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }

    [Fact]
    public void EnsureSchema_ExistingDb_DoesNotReRunSeedInserts()
    {
        var cs = NewDb();
        try
        {
            SqliteSchemaInitializer.Apply(cs);                 // V031이 SYS_ROLE 시드(INSERT)를 1회 수행
            var before = Count(cs, "SYS_ROLE");
            before.Should().BeGreaterThan(0);

            SqliteSchemaInitializer.EnsureSchema(cs);          // 증분 패스는 INSERT를 실행하지 않음

            Count(cs, "SYS_ROLE").Should().Be(before);         // 시드 행 중복 없음
        }
        finally { try { File.Delete(FileOf(cs)); } catch { /* 임시 파일 정리 실패 무시 */ } }
    }
}
