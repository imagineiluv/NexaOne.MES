using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;
using NexaDB.Diagnostics;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.UnitTests.Persistence;

public sealed class DapperQueryGatewayTests
{
    [Fact]
    public async Task QueryAsync_PassesTimeoutAndCancellationToCommand_AndEmitsSafeDiagnostics()
    {
        var provider = new TrackingSqliteProvider();
        var sink = new InMemoryDiagnosticEventSink();
        var gateway = CreateGateway(
            provider,
            sink,
            new DapperQueryGatewayOptions
            {
                CommandTimeoutSeconds = 17,
                Module = "EST"
            });
        using var cancellation = new CancellationTokenSource();

        var rows = await gateway.QueryAsync<string>(
            "SELECT @SensitiveEquipmentId",
            new { SensitiveEquipmentId = "EQ-SECRET-42" },
            cancellation.Token);

        rows.Should().Equal("EQ-SECRET-42");
        provider.LastCommandTimeout.Should().Be(17);
        provider.LastCommandCancellationToken.Should().Be(cancellation.Token);

        var diagnostic = sink.Snapshot().Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Information);
        diagnostic.Duration.Should().NotBeNull();
        diagnostic.Properties["query_kind"].Should().Be("inline");
        diagnostic.Properties["query_identifier"].Should().BeOfType<string>()
            .Which.Should().StartWith("inline:");
        diagnostic.Properties["query_fingerprint"].Should().BeOfType<string>()
            .Which.Should().HaveLength(32);
        diagnostic.Properties["provider"].Should().Be("Sqlite");
        diagnostic.Properties["module"].Should().Be("EST");
        diagnostic.Properties["row_count"].Should().Be(1);
        diagnostic.Properties["outcome"].Should().Be("succeeded");
        diagnostic.Properties["command_timeout_seconds"].Should().Be(17);

        AssertDoesNotContainSensitiveData(
            diagnostic,
            "SELECT",
            "SensitiveEquipmentId",
            "EQ-SECRET-42");
    }

    [Fact]
    public async Task QueryNamedAsync_UsesHashedNameInsteadOfPublishingCatalogName()
    {
        var provider = new TrackingSqliteProvider();
        var sink = new InMemoryDiagnosticEventSink();
        var catalog = new InMemoryQueryCatalog();
        catalog.Register("EST.Utility.List", "SELECT 1");
        var gateway = CreateGateway(provider, sink, catalog: catalog);

        var rows = await gateway.QueryNamedAsync<int>("EST.Utility.List");

        rows.Should().Equal(1);
        var diagnostic = sink.Snapshot().Should().ContainSingle().Subject;
        diagnostic.Properties["query_kind"].Should().Be("named");
        diagnostic.Properties["query_identifier"].Should().BeOfType<string>()
            .Which.Should().StartWith("named:").And.NotContain("EST.Utility.List");
        AssertDoesNotContainSensitiveData(diagnostic, "SELECT", "EST.Utility.List");
    }

    [Fact]
    public async Task QueryAsync_WhenCallerCancels_RecordsCanceledWithoutExceptionMessage()
    {
        var provider = new TrackingSqliteProvider();
        var sink = new InMemoryDiagnosticEventSink();
        var gateway = CreateGateway(provider, sink);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var action = () => gateway.QueryAsync<int>("SELECT 1", ct: cancellation.Token);

        await action.Should().ThrowAsync<OperationCanceledException>();
        var diagnostic = sink.Snapshot().Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.ExceptionMessage.Should().BeNull();
        diagnostic.Properties["outcome"].Should().Be("canceled");
    }

    [Fact]
    public async Task QueryAsync_WhenProviderTimesOut_RecordsTimeoutWithoutSensitiveMessage()
    {
        var provider = new TrackingSqliteProvider
        {
            OpenFailure = new TimeoutException("LOT-SECRET-77 could not open")
        };
        var sink = new InMemoryDiagnosticEventSink();
        var gateway = CreateGateway(provider, sink);

        var action = () => gateway.QueryAsync<int>("SELECT 1");

        await action.Should().ThrowAsync<TimeoutException>();
        var diagnostic = sink.Snapshot().Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
        diagnostic.ExceptionType.Should().Be(typeof(TimeoutException).FullName);
        diagnostic.ExceptionMessage.Should().BeNull();
        diagnostic.Properties["outcome"].Should().Be("timed_out");
        AssertDoesNotContainSensitiveData(diagnostic, "LOT-SECRET-77", "SELECT");
    }

    [Fact]
    public async Task QueryAsync_WhenDatabaseFails_RecordsErrorAndPreservesOriginalException()
    {
        var provider = new TrackingSqliteProvider();
        var sink = new InMemoryDiagnosticEventSink();
        var gateway = CreateGateway(provider, sink);

        var action = () => gateway.QueryAsync<int>("SELECT * FROM LOT_SECRET_TABLE");

        await action.Should().ThrowAsync<SqliteException>();
        var diagnostic = sink.Snapshot().Should().ContainSingle().Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.ExceptionType.Should().Be(typeof(SqliteException).FullName);
        diagnostic.ExceptionMessage.Should().BeNull();
        diagnostic.Properties["outcome"].Should().Be("failed");
        AssertDoesNotContainSensitiveData(diagnostic, "LOT_SECRET_TABLE", "SELECT");
    }

    [Fact]
    public async Task QueryAsync_WhenDiagnosticSinkFails_DoesNotChangeDatabaseResult()
    {
        var provider = new TrackingSqliteProvider();
        var gateway = CreateGateway(provider, new ThrowingDiagnosticSink());

        var rows = await gateway.QueryAsync<int>("SELECT 1");

        rows.Should().Equal(1);
    }

    [Fact]
    public async Task DataSourceConfiguration_IsUsedByExistingQueryRepositoryComposition()
    {
        var provider = new TrackingSqliteProvider();
        var sink = new InMemoryDiagnosticEventSink();
        var dataSource = new EesDataSource
        {
            Provider = provider,
            ConnectionString = "Data Source=:memory:",
            QueryGatewayOptions = new DapperQueryGatewayOptions
            {
                CommandTimeoutSeconds = 23,
                Module = "COMMON"
            },
            QueryDiagnosticSink = sink
        };
        var repository = new TestQueryRepository(dataSource);

        var rows = await repository.ReadAsync<int>("SELECT 1");

        rows.Should().Equal(1);
        provider.LastCommandTimeout.Should().Be(23);
        var diagnostic = sink.Snapshot().Should().ContainSingle().Subject;
        diagnostic.Properties["module"].Should().Be("COMMON");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveCommandTimeout(int timeoutSeconds)
    {
        var action = () => CreateGateway(
            new TrackingSqliteProvider(),
            options: new DapperQueryGatewayOptions { CommandTimeoutSeconds = timeoutSeconds });

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static DapperQueryGateway CreateGateway(
        IDatabaseProvider provider,
        IDiagnosticEventSink? sink = null,
        DapperQueryGatewayOptions? options = null,
        IQueryCatalog? catalog = null) =>
        new(
            new EesDataSource
            {
                Provider = provider,
                ConnectionString = "Data Source=:memory:"
            },
            catalog,
            options,
            sink);

    private static void AssertDoesNotContainSensitiveData(
        DiagnosticEvent diagnostic,
        params string[] sensitiveValues)
    {
        var serialized = JsonSerializer.Serialize(diagnostic);
        foreach (var sensitiveValue in sensitiveValues)
        {
            serialized.Should().NotContain(sensitiveValue);
        }
    }

    private sealed class ThrowingDiagnosticSink : IDiagnosticEventSink
    {
        public ValueTask WriteAsync(DiagnosticEvent diagnosticEvent, CancellationToken ct = default) =>
            ValueTask.FromException(new InvalidOperationException("diagnostic sink unavailable"));
    }

    private sealed class TestQueryRepository : QueryRepository
    {
        public TestQueryRepository(EesDataSource dataSource)
            : base(dataSource)
        {
        }

        public Task<IReadOnlyList<T>> ReadAsync<T>(string sql, CancellationToken ct = default) =>
            QueryAsync<T>(sql, ct: ct);
    }

    private sealed class TrackingSqliteProvider : IDatabaseProvider
    {
        public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;
        public string Name => "SQLite query gateway test adapter";
        public ProviderCapabilities Capabilities => ProviderCapabilities.None;
        public int? LastCommandTimeout { get; set; }
        public CancellationToken LastCommandCancellationToken { get; set; }
        public Exception? OpenFailure { get; init; }

        public DbConnection CreateConnection(string connectionString) =>
            new TrackingDbConnection(new SqliteConnection(connectionString), this, OpenFailure);

        public IQueryExecutor QueryExecutor => throw new NotSupportedException();
        public IMetadataProvider MetadataProvider => throw new NotSupportedException();
        public ITransactionManager TransactionManager => throw new NotSupportedException();
        public IChangeFeedProvider ChangeFeedProvider => throw new NotSupportedException();
    }

    private sealed class TrackingDbConnection : DbConnection
    {
        private readonly DbConnection _inner;
        private readonly TrackingSqliteProvider _provider;
        private readonly Exception? _openFailure;

        public TrackingDbConnection(
            DbConnection inner,
            TrackingSqliteProvider provider,
            Exception? openFailure)
        {
            _inner = inner;
            _provider = provider;
            _openFailure = openFailure;
        }

        [AllowNull]
        public override string ConnectionString
        {
            get => _inner.ConnectionString;
            set => _inner.ConnectionString = value;
        }

        public override string Database => _inner.Database;
        public override string DataSource => _inner.DataSource;
        public override string ServerVersion => _inner.ServerVersion;
        public override ConnectionState State => _inner.State;

        public override void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
        public override void Close() => _inner.Close();
        public override void Open() => _inner.Open();

        public override Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _openFailure is null
                ? _inner.OpenAsync(cancellationToken)
                : Task.FromException(_openFailure);
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            _inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() =>
            new TrackingDbCommand(_inner.CreateCommand(), this, _provider);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class TrackingDbCommand : DbCommand
    {
        private readonly DbCommand _inner;
        private readonly TrackingDbConnection _owner;
        private readonly TrackingSqliteProvider _provider;

        public TrackingDbCommand(
            DbCommand inner,
            TrackingDbConnection owner,
            TrackingSqliteProvider provider)
        {
            _inner = inner;
            _owner = owner;
            _provider = provider;
        }

        [AllowNull]
        public override string CommandText
        {
            get => _inner.CommandText;
            set => _inner.CommandText = value;
        }

        public override int CommandTimeout
        {
            get => _inner.CommandTimeout;
            set
            {
                _provider.LastCommandTimeout = value;
                _inner.CommandTimeout = value;
            }
        }

        public override CommandType CommandType
        {
            get => _inner.CommandType;
            set => _inner.CommandType = value;
        }

        public override bool DesignTimeVisible
        {
            get => _inner.DesignTimeVisible;
            set => _inner.DesignTimeVisible = value;
        }

        public override UpdateRowSource UpdatedRowSource
        {
            get => _inner.UpdatedRowSource;
            set => _inner.UpdatedRowSource = value;
        }

        protected override DbConnection? DbConnection
        {
            get => _owner;
            set
            {
                if (value is null)
                {
                    _inner.Connection = null;
                }
                else if (!ReferenceEquals(value, _owner))
                {
                    throw new InvalidOperationException("Unexpected connection adapter.");
                }
            }
        }

        protected override DbParameterCollection DbParameterCollection => _inner.Parameters;

        protected override DbTransaction? DbTransaction
        {
            get => _inner.Transaction;
            set => _inner.Transaction = value;
        }

        public override void Cancel() => _inner.Cancel();
        public override int ExecuteNonQuery() => _inner.ExecuteNonQuery();
        public override object? ExecuteScalar() => _inner.ExecuteScalar();
        public override void Prepare() => _inner.Prepare();
        protected override DbParameter CreateDbParameter() => _inner.CreateParameter();
        protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
            _inner.ExecuteReader(behavior);

        protected override Task<DbDataReader> ExecuteDbDataReaderAsync(
            CommandBehavior behavior,
            CancellationToken cancellationToken)
        {
            _provider.LastCommandCancellationToken = cancellationToken;
            return _inner.ExecuteReaderAsync(behavior, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
