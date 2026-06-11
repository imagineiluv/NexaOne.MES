using System.Data;
using Dapper;
using NexusCom.Data.Abstractions.Interfaces;
using NexusCom.Data.Abstractions.Models;

namespace NexaOne.Infrastructure.Persistence;

public sealed class ServiceObjectProcessor
{
    private readonly ITransactionManager _txnManager;
    private readonly DatabaseEndpoint _endpoint;
    private readonly string _currentUser;

    public ServiceObjectProcessor(EesDataSource dataSource, string currentUser = "SYSTEM")
    {
        _txnManager = dataSource.Provider.TransactionManager;
        _endpoint = dataSource.CreateEndpoint();
        _currentUser = currentUser;
    }

    public Task<int> InsertAsync(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
        {
            var enriched = InjectAudit(param, isInsert: true);
            return await conn.ExecuteAsync(sql, enriched, txn).ConfigureAwait(false);
        }, IsolationLevel.ReadCommitted, ct);

    public Task<int> UpdateAsync(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
        {
            var enriched = InjectAudit(param, isInsert: false);
            return await conn.ExecuteAsync(sql, enriched, txn).ConfigureAwait(false);
        }, IsolationLevel.ReadCommitted, ct);

    public Task<int> DeleteAsync(
        string sql,
        object? param = null,
        CancellationToken ct = default) =>
        _txnManager.ExecuteInTransactionAsync(_endpoint, async (conn, txn) =>
            await conn.ExecuteAsync(sql, param, txn).ConfigureAwait(false),
        IsolationLevel.ReadCommitted, ct);

    private Dictionary<string, object?> InjectAudit(object? param, bool isInsert)
    {
        var dict = param is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(
                param.GetType().GetProperties()
                    .ToDictionary(p => p.Name, p => p.GetValue(param)));

        var now = DateTime.UtcNow;
        if (isInsert)
        {
            dict["CreatedBy"] = _currentUser;
            dict["CreatedAt"] = now;
        }
        dict["UpdatedBy"] = _currentUser;
        dict["UpdatedAt"] = now;
        return dict;
    }
}
