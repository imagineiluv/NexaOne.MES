using Microsoft.Data.SqlClient;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexusCom.Data.MsSql;

public sealed class MsSqlMetadataProvider : IMetadataProvider
{
    public async Task<IReadOnlyList<DatabaseInfo>> GetDatabasesAsync(
        DatabaseEndpoint endpoint, CancellationToken ct = default)
    {
        var result = new List<DatabaseInfo>();
        await using var conn = await OpenAsync(endpoint, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name";
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new DatabaseInfo(reader.GetString(0)));
        return result;
    }

    public async Task<IReadOnlyList<SchemaInfo>> GetSchemasAsync(
        DatabaseEndpoint endpoint, CancellationToken ct = default)
    {
        var result = new List<SchemaInfo>();
        await using var conn = await OpenAsync(endpoint, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT SCHEMA_NAME, SCHEMA_OWNER
            FROM INFORMATION_SCHEMA.SCHEMATA
            WHERE SCHEMA_NAME NOT IN ('sys','INFORMATION_SCHEMA','guest','db_owner','db_accessadmin',
                                       'db_securityadmin','db_ddladmin','db_backupoperator','db_datareader',
                                       'db_datawriter','db_denydatareader','db_denydatawriter')
            ORDER BY SCHEMA_NAME
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new SchemaInfo(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
        return result;
    }

    public async Task<IReadOnlyList<TableInfo>> GetTablesAsync(
        DatabaseEndpoint endpoint, string? schema = null, CancellationToken ct = default)
    {
        var result = new List<TableInfo>();
        await using var conn = await OpenAsync(endpoint, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE
            FROM INFORMATION_SCHEMA.TABLES
            WHERE (@schema IS NULL OR TABLE_SCHEMA = @schema)
            ORDER BY TABLE_SCHEMA, TABLE_NAME
            """;
        cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            result.Add(new TableInfo(reader.GetString(1), reader.GetString(0), reader.GetString(2)));
        return result;
    }

    public async Task<IReadOnlyList<ColumnInfo>> GetColumnsAsync(
        DatabaseEndpoint endpoint, string? schema, string table, CancellationToken ct = default)
    {
        var result = new List<ColumnInfo>();
        await using var conn = await OpenAsync(endpoint, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COLUMN_NAME, DATA_TYPE, IS_NULLABLE, ORDINAL_POSITION,
                   CHARACTER_MAXIMUM_LENGTH, NUMERIC_PRECISION, NUMERIC_SCALE
            FROM INFORMATION_SCHEMA.COLUMNS
            WHERE TABLE_SCHEMA = ISNULL(@schema, SCHEMA_NAME())
              AND TABLE_NAME = @table
            ORDER BY ORDINAL_POSITION
            """;
        cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            result.Add(new ColumnInfo(
                Name: reader.GetString(0),
                DataType: reader.GetString(1),
                IsNullable: string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase),
                Ordinal: reader.GetInt32(3),
                MaxLength: reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Precision: reader.IsDBNull(5) ? null : (int?)reader.GetByte(5),
                Scale: reader.IsDBNull(6) ? null : (int?)reader.GetInt32(6)));
        }
        return result;
    }

    public async Task<IReadOnlyList<IndexInfo>> GetIndexesAsync(
        DatabaseEndpoint endpoint, string? schema, string table, CancellationToken ct = default)
    {
        var result = new List<IndexInfo>();
        await using var conn = await OpenAsync(endpoint, ct).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT i.name, i.is_unique, c.name AS column_name
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
            JOIN sys.tables t ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = ISNULL(@schema, SCHEMA_NAME())
              AND t.name = @table
              AND i.name IS NOT NULL
            ORDER BY i.name, ic.key_ordinal
            """;
        cmd.Parameters.AddWithValue("@schema", (object?)schema ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@table", table);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var groups = new Dictionary<string, (bool isUnique, List<string> cols)>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var name = reader.GetString(0);
            var isUnique = reader.GetBoolean(1);
            var col = reader.GetString(2);
            if (!groups.TryGetValue(name, out var entry))
                groups[name] = entry = (isUnique, new List<string>());
            entry.cols.Add(col);
        }

        foreach (var kv in groups)
            result.Add(new IndexInfo(kv.Key, kv.Value.isUnique, kv.Value.cols));

        return result;
    }

    private static async Task<SqlConnection> OpenAsync(DatabaseEndpoint endpoint, CancellationToken ct)
    {
        var conn = new SqlConnection(endpoint.ConnectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);
        return conn;
    }
}
