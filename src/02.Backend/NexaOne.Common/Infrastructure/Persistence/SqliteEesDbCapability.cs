using System.Data.Common;
using Dapper;
using NexaDB.Data.Abstractions.Interfaces;

namespace NexaOne.Infrastructure.Persistence;

/// <summary>
/// SQLite 방언 INexaOneEESDbCapability 구현 — 테스트/로컬 실행용(MSSQL 대체).
/// NexaDB.Data.Sqlite의 SqliteProvider(IDatabaseProvider)와 함께 등록한다.
/// MsSqlProvider가 자체적으로 capability를 구현하는 것과 달리 SQLite는 본 어댑터로 분리한다.
/// </summary>
public sealed class SqliteEesDbCapability : INexaOneEESDbCapability
{
    // SQLite는 테이블 힌트가 없다(MSSQL WITH(NOLOCK) 대체 — 빈 문자열).
    public string NoLockHint => string.Empty;

    // SQLite는 시퀀스를 지원하지 않는다(AUTOINCREMENT/사용처 없음).
    public string GetSequenceSql(string sequenceName) =>
        throw new NotSupportedException("SQLite does not support sequences.");

    public string WrapPaged(string baseSql, string orderBy, int offset, int limit)
    {
        // SQLite는 OFFSET에 ORDER BY가 필수는 아니나, 결정적 페이징을 위해 있으면 사용한다.
        var order = string.IsNullOrWhiteSpace(orderBy) ? string.Empty : $" ORDER BY {orderBy}";
        return $"{baseSql}{order} LIMIT {limit} OFFSET {offset}";
    }

    public string BuildUpsertSql(
        string tableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> dataColumns,
        IReadOnlyList<string>? insertOnlyColumns = null)
    {
        var insertOnly = insertOnlyColumns ?? Array.Empty<string>();
        // insertOnly는 INSERT VALUES에는 포함하되 DO UPDATE SET에서는 제외한다(최초 기록 후 갱신 시 보존).
        var allColumns = keyColumns.Concat(dataColumns).Concat(insertOnly).ToList();
        var insertCols = string.Join(", ", allColumns.Select(Quote));
        var insertVals = string.Join(", ", allColumns.Select(c => $"@{c}"));
        var conflictCols = string.Join(", ", keyColumns.Select(Quote));
        var updateSet = string.Join(", ", dataColumns.Select(c => $"{Quote(c)} = excluded.{Quote(c)}"));

        // SQLite UPSERT(ON CONFLICT ... DO UPDATE) — MSSQL MERGE 대응. 'excluded'는 충돌 행 별칭.
        return $"""
            INSERT INTO {Quote(tableName)} ({insertCols})
            VALUES ({insertVals})
            ON CONFLICT ({conflictCols}) DO UPDATE SET {updateSet}
            """;
    }

    public async Task BulkInsertAsync(
        DbConnection connection,
        string tableName,
        IEnumerable<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken ct = default)
    {
        var rowList = rows.ToList();
        if (rowList.Count == 0) return;

        var columns = rowList[0].Keys.ToList();
        var colList = string.Join(", ", columns.Select(Quote));
        var paramList = string.Join(", ", columns.Select(c => $"@{c}"));
        var sql = $"INSERT INTO {Quote(tableName)} ({colList}) VALUES ({paramList})";

        // SQLite는 SqlBulkCopy가 없어 단건 INSERT 반복(테스트/소량 적재 용도). 호출부가 트랜잭션을 감싼다.
        foreach (var row in rowList)
        {
            var p = new DynamicParameters();
            foreach (var c in columns) p.Add($"@{c}", row.TryGetValue(c, out var v) ? v : null);
            await connection.ExecuteAsync(new CommandDefinition(sql, p, cancellationToken: ct)).ConfigureAwait(false);
        }
    }

    public string GetCurrentTimeSql => "datetime('now')";

    public string NullCoalesceFn => "IFNULL";

    public string ConcatColumns(string left, string right) => $"{left} || {right}";

    public string BuildBatchInsertSql(string tableName, IReadOnlyList<string> columns, int rowCount)
    {
        if (rowCount <= 0) throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (columns.Count == 0) throw new ArgumentException("columns must not be empty.", nameof(columns));

        var colList = string.Join(", ", columns.Select(Quote));
        var rows = Enumerable.Range(0, rowCount).Select(i =>
            $"({string.Join(", ", columns.Select(c => $"@{c}_{i}"))})");
        return $"INSERT INTO {Quote(tableName)} ({colList}) VALUES {string.Join(", ", rows)}";
    }

    public string WrapTempTable(string tempTableName, string selectSql) =>
        $"CREATE TEMP TABLE {Quote(tempTableName)} AS {selectSql}";

    // SQLite에는 DMV가 없다 — 진단 쿼리는 무해한 빈 결과를 반환한다(사용처 없음).
    public string GetLongRunningSql => "SELECT NULL AS session_id WHERE 1 = 0";

    public string GetActiveSessionsSql => "SELECT 1";

    /// <summary>SQLite 식별자 인용(표준 큰따옴표, 내부 따옴표 이스케이프). 값은 항상 파라미터화한다.</summary>
    private static string Quote(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
