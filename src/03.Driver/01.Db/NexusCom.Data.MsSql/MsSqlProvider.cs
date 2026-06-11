using System.Data.Common;
using System.Text;
using Microsoft.Data.SqlClient;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;
using NexaOne.Infrastructure.Persistence;

namespace NexusCom.Data.MsSql;

public sealed class MsSqlProvider : IDatabaseProvider, INexaOneEESDbCapability
{
    public MsSqlProvider()
    {
        Kind = DatabaseProviderKind.SqlServer;
        Name = "SQL Server";
        Capabilities = new ProviderCapabilities(
            SupportsTransactions: true,
            SupportsSchemas: true,
            SupportsNotifications: false,
            SupportsCdc: true,
            SupportsStreaming: true,
            SupportsBatching: true,
            SupportsPropertyBeforeAfter: true,
            SupportsServerSidePaging: true,
            SupportsParameterizedCommands: true);

        QueryExecutor = new MsSqlQueryExecutor();
        MetadataProvider = new MsSqlMetadataProvider();
        TransactionManager = new MsSqlTransactionManager();
        ChangeFeedProvider = new MsSqlChangeFeedProvider();
    }

    public DatabaseProviderKind Kind { get; }
    public string Name { get; }
    public ProviderCapabilities Capabilities { get; }
    public IQueryExecutor QueryExecutor { get; }
    public IMetadataProvider MetadataProvider { get; }
    public ITransactionManager TransactionManager { get; }
    public IChangeFeedProvider ChangeFeedProvider { get; }

    public DbConnection CreateConnection(string connectionString) =>
        new SqlConnection(connectionString);

    // INexaOneEESDbCapability

    public string NoLockHint => "WITH (NOLOCK)";

    public string GetSequenceSql(string sequenceName) =>
        $"SELECT NEXT VALUE FOR [{sequenceName}]";

    public string WrapPaged(string baseSql, int offset, int limit) =>
        $"{baseSql} ORDER BY (SELECT NULL) OFFSET {offset} ROWS FETCH NEXT {limit} ROWS ONLY";

    public string BuildUpsertSql(
        string tableName,
        IReadOnlyList<string> keyColumns,
        IReadOnlyList<string> dataColumns)
    {
        var allColumns = keyColumns.Concat(dataColumns).ToList();
        var matchCond = string.Join(" AND ", keyColumns.Select(c => $"target.[{c}] = source.[{c}]"));
        var updateSet = string.Join(", ", dataColumns.Select(c => $"target.[{c}] = source.[{c}]"));
        var insertCols = string.Join(", ", allColumns.Select(c => $"[{c}]"));
        var insertVals = string.Join(", ", allColumns.Select(c => $"source.[{c}]"));

        // HOLDLOCK(SERIALIZABLE): 동시 업서트 시 MERGE race로 인한 PK 중복 오류 방지.
        // (ConditionSettingRepository/EquipmentStateRepository의 수동 MERGE와 동일한 규약)
        return $"""
            MERGE INTO [{tableName}] WITH (HOLDLOCK) AS target
            USING (VALUES ({string.Join(", ", allColumns.Select(c => $"@{c}"))}))
                AS source ({insertCols})
            ON ({matchCond})
            WHEN MATCHED THEN
                UPDATE SET {updateSet}
            WHEN NOT MATCHED THEN
                INSERT ({insertCols}) VALUES ({insertVals});
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

        var sqlConn = (SqlConnection)connection;
        using var bulk = new SqlBulkCopy(sqlConn)
        {
            DestinationTableName = tableName,
            BulkCopyTimeout = 60
        };

        var columns = rowList[0].Keys.ToList();
        var table = new System.Data.DataTable(tableName);

        foreach (var col in columns)
        {
            table.Columns.Add(col);
            bulk.ColumnMappings.Add(col, col);
        }

        foreach (var row in rowList)
        {
            var dataRow = table.NewRow();
            foreach (var col in columns)
                dataRow[col] = row[col] ?? DBNull.Value;
            table.Rows.Add(dataRow);
        }

        await bulk.WriteToServerAsync(table, ct).ConfigureAwait(false);
    }
}
