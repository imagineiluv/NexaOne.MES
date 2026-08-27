using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Persistence;

public sealed class ServiceObjectProcessorTests
{
    [Fact]
    public async Task ExecuteManyAsync_WhenInFlightWriteIsCanceled_PropagatesCancellationAndRollsBack()
    {
        var database = new CancelableWriteDatabase();
        var processor = new ServiceObjectProcessor(
            CreateDataSource(database, commandTimeoutSeconds: 37));
        using var cancellation = new CancellationTokenSource();

        var execution = processor.ExecuteManyAsync(
            cancellation.Token,
            (CancelableWriteDatabase.CompletedWriteSql, null),
            (CancelableWriteDatabase.BlockingWriteSql, null));

        await database.BlockingWriteStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);

        exception.CancellationToken.Should().Be(cancellation.Token);
        database.LastCommandCancellationToken.Should().Be(cancellation.Token);
        database.LastCommandTimeout.Should().Be(37);
        database.CommitCount.Should().Be(0);
        database.RollbackCount.Should().Be(1);
        database.PendingWrites.Should().Be(0);
        database.PersistedWrites.Should().Be(0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveCommandTimeout(int commandTimeoutSeconds)
    {
        var database = new CancelableWriteDatabase();

        var action = () => new ServiceObjectProcessor(
            CreateDataSource(database, commandTimeoutSeconds));

        action.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("dataSource");
    }

    private static EesDataSource CreateDataSource(
        CancelableWriteDatabase database,
        int commandTimeoutSeconds) =>
        new()
        {
            Provider = new CancelableWriteProvider(database),
            ConnectionString = "Data Source=service-object-processor-test",
            QueryGatewayOptions = new DapperQueryGatewayOptions
            {
                CommandTimeoutSeconds = commandTimeoutSeconds
            }
        };

    private sealed class CancelableWriteDatabase
    {
        public const string CompletedWriteSql = "completed-write";
        public const string BlockingWriteSql = "blocking-write";

        public TaskCompletionSource BlockingWriteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PendingWrites { get; set; }
        public int PersistedWrites { get; set; }
        public int CommitCount { get; set; }
        public int RollbackCount { get; set; }
        public int? LastCommandTimeout { get; set; }
        public CancellationToken LastCommandCancellationToken { get; set; }
    }

    private sealed class CancelableWriteProvider(CancelableWriteDatabase database) : IDatabaseProvider
    {
        public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;
        public string Name => "Cancelable write test provider";
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public DbConnection CreateConnection(string connectionString) =>
            throw new NotSupportedException();
        public IQueryExecutor QueryExecutor => throw new NotSupportedException();
        public IMetadataProvider MetadataProvider => throw new NotSupportedException();
        public ITransactionManager TransactionManager { get; } =
            new CancelableWriteTransactionManager(database);
        public IChangeFeedProvider ChangeFeedProvider => throw new NotSupportedException();
    }

    private sealed class CancelableWriteTransactionManager(CancelableWriteDatabase database)
        : ITransactionManager
    {
        public async Task<TResult> ExecuteInTransactionAsync<TResult>(
            DatabaseEndpoint endpoint,
            Func<DbConnection, DbTransaction, Task<TResult>> action,
            IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
            CancellationToken ct = default)
        {
            await using var connection = new CancelableWriteConnection(database);
            await using var transaction = new CancelableWriteTransaction(connection, database);

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

    private sealed class CancelableWriteConnection(CancelableWriteDatabase database) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "service-object-processor-test";
        public override string DataSource => "test";
        public override string ServerVersion => "1.0";
        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) =>
            throw new NotSupportedException();
        public override void Close() { }
        public override void Open() { }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            new CancelableWriteTransaction(this, database);

        protected override DbCommand CreateDbCommand() =>
            new CancelableWriteCommand(this, database);
    }

    private sealed class CancelableWriteTransaction(
        CancelableWriteConnection connection,
        CancelableWriteDatabase database) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;

        public override void Commit()
        {
            database.CommitCount++;
            database.PersistedWrites += database.PendingWrites;
            database.PendingWrites = 0;
        }

        public override void Rollback()
        {
            database.RollbackCount++;
            database.PendingWrites = 0;
        }
    }

    private sealed class CancelableWriteCommand(
        CancelableWriteConnection connection,
        CancelableWriteDatabase database) : DbCommand
    {
        private readonly SqliteCommand _parameterOwner = new();

        [AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = connection;
        protected override DbParameterCollection DbParameterCollection => _parameterOwner.Parameters;
        protected override DbTransaction? DbTransaction { get; set; }

        public override void Cancel() { }
        public override int ExecuteNonQuery() => throw new NotSupportedException();
        public override object? ExecuteScalar() => throw new NotSupportedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new SqliteParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            throw new NotSupportedException();

        public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
        {
            database.LastCommandCancellationToken = cancellationToken;
            database.LastCommandTimeout = CommandTimeout;

            if (CommandText == CancelableWriteDatabase.CompletedWriteSql)
            {
                database.PendingWrites++;
                return 1;
            }

            if (CommandText != CancelableWriteDatabase.BlockingWriteSql)
            {
                throw new InvalidOperationException($"Unexpected SQL: {CommandText}");
            }

            database.BlockingWriteStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _parameterOwner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
