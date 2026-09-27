using System.Data;
using System.Data.Common;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.ServerTests;

/// <summary>Injects a lost response only after the real provider has committed successfully.</summary>
internal sealed class AfterCommitResponseLossProvider(IDatabaseProvider inner) : IDatabaseProvider
{
    private readonly ResponseLossTransactionManager _transactions = new(inner.TransactionManager);

    public int CallbackCount => _transactions.CallbackCount;
    public DatabaseProviderKind Kind => inner.Kind;
    public string Name => inner.Name;
    public ProviderCapabilities Capabilities => inner.Capabilities;
    public DbConnection CreateConnection(string connectionString) => inner.CreateConnection(connectionString);
    public IQueryExecutor QueryExecutor => inner.QueryExecutor;
    public IMetadataProvider MetadataProvider => inner.MetadataProvider;
    public ITransactionManager TransactionManager => _transactions;
    public IChangeFeedProvider ChangeFeedProvider => inner.ChangeFeedProvider;

    private sealed class ResponseLossTransactionManager(ITransactionManager inner) : ITransactionManager
    {
        private int _callbackCount;
        public int CallbackCount => Volatile.Read(ref _callbackCount);

        public async Task<TResult> ExecuteInTransactionAsync<TResult>(DatabaseEndpoint endpoint,
            Func<DbConnection, DbTransaction, Task<TResult>> action,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted, CancellationToken ct = default)
        {
            await inner.ExecuteInTransactionAsync(endpoint, async (connection, transaction) =>
            {
                Interlocked.Increment(ref _callbackCount);
                return await action(connection, transaction);
            }, isolationLevel, ct);
            throw new IOException("Injected response loss after successful provider commit.");
        }
    }
}
