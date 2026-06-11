using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexusCom.Data.MsSql;

public sealed class MsSqlQueryExecutor : IQueryExecutor
{
    public async Task<int> ExecuteNonQueryAsync(
        DatabaseEndpoint endpoint,
        QueryRequest request,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(endpoint, ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, request);
        return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task<object?> ExecuteScalarAsync(
        DatabaseEndpoint endpoint,
        QueryRequest request,
        CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(endpoint, ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, request);
        var result = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is DBNull ? null : result;
    }

    public async Task<QueryResult> ExecuteReaderAsync(
        DatabaseEndpoint endpoint,
        QueryRequest request,
        CancellationToken ct = default)
    {
        var rows = new List<IReadOnlyDictionary<string, object?>>();

        await foreach (var row in StreamAsync(endpoint, request, ct).ConfigureAwait(false))
        {
            rows.Add(row);
        }

        var columns = rows.Count == 0 ? Array.Empty<string>() : rows[0].Keys.ToArray();
        return new QueryResult(columns, rows, rows.Count);
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> StreamAsync(
        DatabaseEndpoint endpoint,
        QueryRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using var connection = await OpenConnectionAsync(endpoint, ct).ConfigureAwait(false);
        await using var command = CreateCommand(connection, request);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            yield return ReadRow(reader);
        }
    }

    private static async Task<SqlConnection> OpenConnectionAsync(DatabaseEndpoint endpoint, CancellationToken ct)
    {
        var connection = new SqlConnection(endpoint.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        return connection;
    }

    private static SqlCommand CreateCommand(SqlConnection connection, QueryRequest request)
    {
        var command = connection.CreateCommand();
        command.CommandText = request.Sql;
        command.CommandType = request.CommandType;

        if (request.TimeoutSeconds.HasValue)
            command.CommandTimeout = request.TimeoutSeconds.Value;

        foreach (var parameter in request.Parameters)
        {
            var dbParameter = command.CreateParameter();
            dbParameter.ParameterName = NormalizeParameterName(parameter.Name);
            dbParameter.Value = parameter.Value ?? DBNull.Value;
            dbParameter.Direction = parameter.Direction;
            if (parameter.DbType.HasValue)
                dbParameter.DbType = parameter.DbType.Value;

            command.Parameters.Add(dbParameter);
        }

        return command;
    }

    private static IReadOnlyDictionary<string, object?> ReadRow(DbDataReader reader)
    {
        var row = new Dictionary<string, object?>(reader.FieldCount, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return row;
    }

    private static string NormalizeParameterName(string name) =>
        string.IsNullOrWhiteSpace(name) || name[0] == '@' ? name : $"@{name}";
}
