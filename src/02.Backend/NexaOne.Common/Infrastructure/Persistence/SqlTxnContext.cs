using System.Data;
using System.Data.Common;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.Infrastructure.Persistence;

public sealed class SqlTxnContext
{
    private readonly ITransactionManager _txnManager;

    public SqlTxnContext(ITransactionManager txnManager)
    {
        _txnManager = txnManager;
    }

    public Task<TResult> ExecuteAsync<TResult>(
        DatabaseEndpoint endpoint,
        Func<DbConnection, DbTransaction, Task<TResult>> action,
        IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(endpoint, action, isolationLevel, ct);

    public async Task<long> NextSequenceValueAsync(
        DatabaseEndpoint endpoint,
        string sequenceName,
        INexaOneEESDbCapability dialect,
        CancellationToken ct = default)
    {
        var sql = dialect.GetSequenceSql(sequenceName);
        return await ExecuteAsync(endpoint, async (conn, _) =>
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            return Convert.ToInt64(result);
        }, ct: ct).ConfigureAwait(false);
    }
}
