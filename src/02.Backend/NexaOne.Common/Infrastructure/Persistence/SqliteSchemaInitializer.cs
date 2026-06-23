using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// db/migrations의 MSSQL DDL을 SQLite 방언으로 변환해 SQLite DB에 스키마를 생성한다.
/// (NVARCHAR→TEXT, DATETIME2→TEXT, BIT→INTEGER, GETUTCDATE()→CURRENT_TIMESTAMP, IDENTITY 제거 등)
/// 실 MSSQL 없이 로컬/테스트로 호스트를 띄우기 위한 경량 부트스트랩 — 운영 스키마와 1:1은 아니나 구조 동등.
/// 통합 테스트(SqliteSchemaBootstrapper)와 NexaOne.Server(SQLite 모드)가 공유하는 단일 구현이다.
/// </summary>
public static class SqliteSchemaInitializer
{
    /// <summary>
    /// 스키마를 보장한다(idempotent). 빈 DB면 전체 마이그레이션을 1회 적용하고(시드·ALTER 포함),
    /// 이미 사용자 테이블이 있으면 '새로 추가된 마이그레이션의 누락 테이블'만 증분 생성한다.
    /// 후자 덕분에 기존 DB에 신규 마이그레이션(예: 새 모듈 테이블)을 추가해도 재기동 시 자동 생성된다
    /// (과거에는 테이블이 하나라도 있으면 전부 건너뛰어 신규 테이블이 영영 생성되지 않았다).
    /// </summary>
    public static void EnsureSchema(string connectionString)
    {
        if (HasUserTables(connectionString))
        {
            CreateMissingTables(connectionString);
            return;
        }
        Apply(connectionString);
    }

    /// <summary>
    /// 모든 마이그레이션을 적용한다(빈 DB 가정 — CREATE TABLE에 IF NOT EXISTS 없음).
    /// 통합 테스트는 매번 새 임시 DB를 만들므로 이 경로를 직접 쓴다.
    /// </summary>
    public static void Apply(string connectionString)
    {
        var dir = FindMigrationsDir();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        // FK는 단순화를 위해 비강제(마이그레이션 순서·일부 교차참조 무시). 운영(MSSQL)은 FK를 그대로 강제한다.
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

    /// <summary>
    /// 이미 테이블이 있는 DB에 누락 테이블/인덱스만 멱등 생성한다(CREATE TABLE/INDEX는 IF NOT EXISTS로 변환됨).
    /// ALTER(컬럼 추가/변경)·INSERT(1회성 시드)는 건너뛴다 — 기존 데이터·시드를 보존하면서 신규 테이블만 추가한다.
    /// 컬럼 추가형 스키마 진화는 빈 DB 전체 적용 경로에서만 반영된다(증분 패스의 의도적 한계).
    /// </summary>
    public static void CreateMissingTables(string connectionString)
    {
        var dir = FindMigrationsDir();
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        Exec(conn, "PRAGMA foreign_keys = OFF;");

        foreach (var file in Directory.GetFiles(dir, "V*.sql").OrderBy(f => f, StringComparer.Ordinal))
        {
            var ddl = ToSqlite(File.ReadAllText(file));
            foreach (var stmt in ddl.Split(';').Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                // 증분 생성 패스 — 테이블/인덱스 생성문만(IF NOT EXISTS라 멱등). ALTER/INSERT/기타는 제외.
                if (!Regex.IsMatch(stmt, @"\bCREATE\s+(TABLE|(?:UNIQUE\s+)?INDEX)\b", RegexOptions.IgnoreCase))
                    continue;
                try
                {
                    Exec(conn, stmt);
                }
                catch (SqliteException ex)
                {
                    throw new InvalidOperationException(
                        $"SQLite 증분 스키마 생성 실패 @ {Path.GetFileName(file)}: {ex.Message}", ex);
                }
            }
        }
    }

    private static bool HasUserTables(string connectionString)
    {
        using var conn = new SqliteConnection(connectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        var count = Convert.ToInt64(cmd.ExecuteScalar() ?? 0L);
        return count > 0;
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string FindMigrationsDir()
    {
        // 출력 디렉터리에 복사된 사본(db/migrations)을 우선 탐지하고, 없으면 상위로 올라가며 리포 루트를 찾는다.
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
        // MSSQL ALTER COLUMN(타입/길이 변경)은 SQLite 미지원 + TEXT는 길이 무개념이라 무의미 → 문장 제거(무해).
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+\w+\s+ALTER\s+COLUMN\s+.+?;", "", O | RegexOptions.Singleline);
        // MSSQL 다중컬럼 ALTER TABLE t ADD c1 ..., c2 ...; → SQLite 단일 ADD COLUMN 반복
        s = Regex.Replace(s, @"ALTER\s+TABLE\s+(\w+)\s+ADD\s+(.+?);", m =>
        {
            var tbl = m.Groups[1].Value;
            var cols = m.Groups[2].Value.Split(',');
            return string.Join("\n", cols.Select(c => $"ALTER TABLE {tbl} ADD COLUMN {c.Trim()};"));
        }, O | RegexOptions.Singleline);
        // 멱등 생성 — CREATE TABLE/INDEX에 IF NOT EXISTS 주입(증분 생성·반복 기동 안전, 빈 DB 적용엔 무해).
        s = Regex.Replace(s, @"\bCREATE\s+TABLE\s+(?!IF\s+NOT\s+EXISTS\b)", "CREATE TABLE IF NOT EXISTS ", O);
        s = Regex.Replace(s, @"\bCREATE\s+(UNIQUE\s+)?INDEX\s+(?!IF\s+NOT\s+EXISTS\b)", "CREATE $1INDEX IF NOT EXISTS ", O);
        return s;
    }
}
