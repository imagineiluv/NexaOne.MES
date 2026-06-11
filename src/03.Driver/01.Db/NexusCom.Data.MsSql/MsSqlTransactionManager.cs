using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexusCom.Data.MsSql;

public sealed class MsSqlTransactionManager : ITransactionManager
{
    public async Task<TResult> ExecuteInTransactionAsync<TResult>(
        DatabaseEndpoint endpoint,
        Func<DbConnection, DbTransaction, Task<TResult>> action,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var connection = new SqlConnection(endpoint.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(isolationLevel, ct).ConfigureAwait(false);

        try
        {
            var result = await action(connection, transaction).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            return result;
        }
        catch
        {
            await transaction.RollbackAsync(ct).ConfigureAwait(false);
            throw;
        }
    }
}
