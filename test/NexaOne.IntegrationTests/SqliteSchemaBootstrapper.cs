using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NexaOne.IntegrationTests;

/// <summary>
/// db/migrations의 MSSQL DDL을 SQLite 방언으로 변환해 테스트용 SQLite DB에 스키마를 생성한다.
/// (NVARCHAR→TEXT, DATETIME2→TEXT, BIT→INTEGER, GETUTCDATE()→CURRENT_TIMESTAMP, IDENTITY 제거 등)
/// 실 MSSQL 없이 통합 테스트를 돌리기 위한 경량 부트스트랩 — 운영 스키마와 1:1은 아니나 구조 동등.
/// </summary>
internal static class SqliteSchemaBootstrapper
{
    public static void Apply(string connectionString)
    {
        var dir = FindMigrationsDir();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        // FK는 테스트 단순화를 위해 비강제(마이그레이션 순서·일부 교차참조 무시)
        Exec(conn, "PRAGMA foreign_keys = OFF;");

        foreach (var file in Directory.GetFiles(dir, "V*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var ddl = ToSqlite(File.ReadAllText(file));
            try
            {
                Exec(conn, ddl);
            }
            catch (SqliteException ex)
            {
                throw new InvalidOperationException(
                    $"SQLite 스키마 생성 실패 @ {Path.GetFileName(file)}: {ex.Message}", ex);
            }
        }
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string FindMigrationsDir()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null)
        {
            var p = Path.Combine(d.FullName, "db", "migrations");
            if (Directory.Exists(p)) return p;
            d = d.Parent;
        }
        throw new DirectoryNotFoundException($"db/migrations를 {AppContext.BaseDirectory}에서 상위로 찾지 못함");
    }

    private static string ToSqlite(string s)
    {
        const RegexOptions O = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        // 문자열 타입
        s = Regex.Replace(s, @"\bN?VARCHAR\s*\(\s*\w+\s*\)", "TEXT", O);
        s = Regex.Replace(s, @"\bN?CHAR\s*\(\s*\w+\s*\)", "TEXT", O);
        s = Regex.Replace(s, @"\bN?TEXT\b", "TEXT", O);
        // 날짜/시간 (정밀도 인자 포함)
        s = Regex.Replace(s, @"\b(DATETIMEOFFSET|DATETIME2|SMALLDATETIME|DATETIME|DATE|TIME)\b(\s*\(\s*\d+\s*\))?", "TEXT", O);
        // 불리언/정수
        s = Regex.Replace(s, @"\bBIT\b", "INTEGER", O);
        s = Regex.Replace(s, @"\b(BIGINT|SMALLINT|TINYINT|INT)\b", "INTEGER", O);
        // 소수/실수
        s = Regex.Replace(s, @"\b(DECIMAL|NUMERIC|MONEY|SMALLMONEY)\b(\s*\(\s*\d+\s*(,\s*\d+\s*)?\))?", "NUMERIC", O);
        s = Regex.Replace(s, @"\b(FLOAT|REAL)\b(\s*\(\s*\d+\s*\))?", "REAL", O);
        s = Regex.Replace(s, @"\bUNIQUEIDENTIFIER\b", "TEXT", O);
        s = Regex.Replace(s, @"\bVARBINARY\s*\(\s*\w+\s*\)", "BLOB", O);
        // IDENTITY 제거
        s = Regex.Replace(s, @"\bIDENTITY\s*\(\s*\d+\s*,\s*\d+\s*\)", "", O);
        s = Regex.Replace(s, @"\bIDENTITY\b", "", O);
        // 시각 함수 → SQLite
        s = Regex.Replace(s, @"\b(GETUTCDATE|SYSUTCDATETIME|SYSDATETIME|GETDATE)\s*\(\s*\)", "CURRENT_TIMESTAMP", O);
        // 명명된 DEFAULT 제약(CONSTRAINT DF_x DEFAULT ...) → 단순 DEFAULT (SQLite ALTER ADD에서 명명 제약 미지원)
        s = Regex.Replace(s, @"CONSTRAINT\s+\w+\s+DEFAULT\b", "DEFAULT", O);
        // MSSQL 다중컬럼 ALTER TABLE t ADD c1 ..., c2 ...; → SQLite 단일 ADD COLUMN 반복
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+(\w+)\s+ADD\s+(.+?);", m =>
        {
            var tbl = m.Groups[1].Value;
            var cols = m.Groups[2].Value.Split(',');
            return string.Join("\n", cols.Select(c => $"ALTER TABLE {tbl} ADD COLUMN {c.Trim()};"));
        }, O | RegexOptions.Singleline);
        return s;
    }
}
