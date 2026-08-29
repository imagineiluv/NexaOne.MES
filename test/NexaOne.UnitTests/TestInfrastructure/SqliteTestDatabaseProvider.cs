using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.UnitTests.TestInfrastructure;

/// <summary>모듈 영속 seam 테스트에서 실제 SQLite 연결/트랜잭션만 제공하는 경량 어댑터다.</summary>
internal sealed class SqliteTestDatabaseProvider : IDatabaseProvider
{
    public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;
    public string Name => "SQLite module test adapter";
    public ProviderCapabilities Capabilities => ProviderCapabilities.None;
    public DbConnection CreateConnection(string connectionString) => new SqliteConnection(connectionString);
    public IQueryExecutor QueryExecutor => throw new NotSupportedException();
    public IMetadataProvider MetadataProvider => throw new NotSupportedException();
    public ITransactionManager TransactionManager { get; } = new SqliteTransactionManager();
    public IChangeFeedProvider ChangeFeedProvider => throw new NotSupportedException();

    private sealed class SqliteTransactionManager : ITransactionManager
    {
        public async Task<TResult> ExecuteInTransactionAsync<TResult>(
            DatabaseEndpoint endpoint,
            Func<DbConnection, DbTransaction, Task<TResult>> action,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken ct = default)
        {
            await using var connection = new SqliteConnection(endpoint.ConnectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(isolationLevel, ct);
            try
            {
                var result = await action(connection, transaction);
                await transaction.CommitAsync(ct);
                return result;
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
    }
}
