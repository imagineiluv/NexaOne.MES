using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using NexaDB.Data.Abstractions.Interfaces;
using NexaDB.Data.Abstractions.Models;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Prc;

namespace NexaOne.UnitTests.Prc;

public sealed class PurchaseOrderPlanningBridgeTests
{
    [Fact]
    public async Task Concurrent_primary_key_winner_replays_the_same_purchase_order()
    {
        using var database = new PurchaseOrderDatabase(FailureMode.UniqueIdentityRace);
        var request = Request();
        var bridge = database.Bridge(request);

        var result = await bridge.EnsureMrpPurchaseOrderAsync(request);

        result.Should().Be(new PurchaseOrderEnsureResult(request.PurchaseOrderId, false));
    }

    [Fact]
    public async Task Arbitrary_database_fault_is_not_collapsed_into_a_successful_replay()
    {
        using var database = new PurchaseOrderDatabase(FailureMode.ArbitraryDatabaseFault);
        var request = Request();
        var bridge = database.Bridge(request);

        var act = () => bridge.EnsureMrpPurchaseOrderAsync(request);

        await act.Should().ThrowAsync<InjectedDbException>()
            .WithMessage("forced storage failure");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_retried_with_a_non_cancelable_token()
    {
        using var cancellation = new CancellationTokenSource();
        using var database = new PurchaseOrderDatabase(
            FailureMode.CallerCancellation,
            cancellation);
        var request = Request();
        var bridge = database.Bridge(request);

        var act = () => bridge.EnsureMrpPurchaseOrderAsync(request, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        cancellation.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Invalid_command_is_rejected_before_storage_is_called()
    {
        using var database = new PurchaseOrderDatabase(FailureMode.ArbitraryDatabaseFault);
        var invalid = Request() with { Quantity = 0m };
        var bridge = database.Bridge(Request());

        var act = () => bridge.EnsureMrpPurchaseOrderAsync(invalid);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithParameterName("request");
    }

    [Fact]
    public async Task Same_identity_with_different_planning_content_is_rejected()
    {
        using var database = new PurchaseOrderDatabase(FailureMode.UniqueIdentityRace);
        var winner = Request();
        var different = winner with { Quantity = winner.Quantity + 1m };
        var bridge = database.Bridge(winner);

        var act = () => bridge.EnsureMrpPurchaseOrderAsync(different);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already owned by a different command*");
    }

    [Fact]
    public async Task Retry_timestamp_does_not_change_stable_command_identity()
    {
        using var database = new PurchaseOrderDatabase(FailureMode.UniqueIdentityRace);
        var winner = Request();
        var retry = winner with { OrderDate = winner.OrderDate.AddMinutes(5) };
        var bridge = database.Bridge(winner);

        var result = await bridge.EnsureMrpPurchaseOrderAsync(retry);

        result.Should().Be(new PurchaseOrderEnsureResult(retry.PurchaseOrderId, false));
    }

    private static MrpPurchaseOrderRequest Request() => new(
        "PO-RACE-001",
        "PLANT01",
        "MRP purchase PO-RACE-001",
        new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc),
        new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
        12m,
        "MAT01",
        "MRP RUN-001 / MPO-001",
        "planner-01");

    private enum FailureMode
    {
        UniqueIdentityRace,
        ArbitraryDatabaseFault,
        CallerCancellation,
    }

    private sealed class PurchaseOrderDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(
            Path.GetTempPath(), $"nexaone-prc-command-{Guid.NewGuid():N}.db");
        private readonly FailureMode _failureMode;
        private readonly CancellationTokenSource? _cancellation;

        public PurchaseOrderDatabase(
            FailureMode failureMode,
            CancellationTokenSource? cancellation = null)
        {
            _failureMode = failureMode;
            _cancellation = cancellation;
            using var connection = new SqliteConnection(ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE PRC_PURCHASE_ORDER (
                    PURCHASE_ORDER_ID TEXT NOT NULL PRIMARY KEY,
                    PLANT_ID TEXT NOT NULL,
                    PURCHASE_ORDER_NAME TEXT NULL,
                    ORDER_DATE TEXT NULL,
                    INCOMING_DATE TEXT NULL,
                    ORDER_QTY NUMERIC NOT NULL,
                    PRODUCT_ID TEXT NULL,
                    STATUS TEXT NOT NULL,
                    DESCRIPTION TEXT NULL,
                    CREATED_BY TEXT NULL,
                    UPDATED_BY TEXT NULL
                );";
            command.ExecuteNonQuery();
        }

        private string ConnectionString => $"Data Source={_path};Foreign Keys=False";

        public IPurchaseOrderPlanningBridge Bridge(MrpPurchaseOrderRequest winner)
        {
            var provider = new FaultingSqliteProvider(
                _failureMode,
                winner,
                _cancellation);
            var dataSource = new EesDataSource
            {
                Provider = provider,
                ConnectionString = ConnectionString,
            };
            return new NexaOne.PRC.Module(dataSource).GetPurchaseOrderPlanningBridge();
        }

        public void Dispose()
        {
            try { File.Delete(_path); } catch { /* best-effort test cleanup */ }
        }

        private sealed class FaultingSqliteProvider : IDatabaseProvider
        {
            public FaultingSqliteProvider(
                FailureMode failureMode,
                MrpPurchaseOrderRequest winner,
                CancellationTokenSource? cancellation)
                => TransactionManager = new FaultingTransactionManager(
                    failureMode,
                    winner,
                    cancellation);

            public DatabaseProviderKind Kind => DatabaseProviderKind.Sqlite;
            public string Name => "SQLite PRC command race test adapter";
            public ProviderCapabilities Capabilities => ProviderCapabilities.None;
            public DbConnection CreateConnection(string connectionString)
                => new SqliteConnection(connectionString);
            public IQueryExecutor QueryExecutor => throw new NotSupportedException();
            public IMetadataProvider MetadataProvider => throw new NotSupportedException();
            public ITransactionManager TransactionManager { get; }
            public IChangeFeedProvider ChangeFeedProvider => throw new NotSupportedException();
        }

        private sealed class FaultingTransactionManager(
            FailureMode failureMode,
            MrpPurchaseOrderRequest winner,
            CancellationTokenSource? cancellation) : ITransactionManager
        {
            public async Task<TResult> ExecuteInTransactionAsync<TResult>(
                DatabaseEndpoint endpoint,
                Func<DbConnection, DbTransaction, Task<TResult>> action,
                IsolationLevel isolationLevel = IsolationLevel.ReadCommitted,
                CancellationToken ct = default)
            {
                await InsertWinnerAsync(endpoint.ConnectionString, winner);

                if (failureMode == FailureMode.ArbitraryDatabaseFault)
                    throw new InjectedDbException("forced storage failure");
                if (failureMode == FailureMode.CallerCancellation)
                {
                    cancellation!.Cancel();
                    throw new OperationCanceledException(ct);
                }

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

            private static async Task InsertWinnerAsync(
                string connectionString,
                MrpPurchaseOrderRequest request)
            {
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = @"
                    INSERT INTO PRC_PURCHASE_ORDER
                    (PURCHASE_ORDER_ID, PLANT_ID, PURCHASE_ORDER_NAME, ORDER_DATE,
                     INCOMING_DATE, ORDER_QTY, PRODUCT_ID, STATUS, DESCRIPTION,
                     CREATED_BY, UPDATED_BY)
                    VALUES
                    (@id, @plant, @name, @orderDate, @incomingDate, @quantity,
                     @product, 'Ordered', @description, @actor, @actor);";
                command.Parameters.AddWithValue("@id", request.PurchaseOrderId);
                command.Parameters.AddWithValue("@plant", request.PlantId);
                command.Parameters.AddWithValue("@name", request.PurchaseOrderName);
                command.Parameters.AddWithValue("@orderDate", request.OrderDate);
                command.Parameters.AddWithValue("@incomingDate", request.IncomingDate!);
                command.Parameters.AddWithValue("@quantity", request.Quantity);
                command.Parameters.AddWithValue("@product", request.ProductId);
                command.Parameters.AddWithValue("@description", request.Description);
                command.Parameters.AddWithValue("@actor", request.ExecutedBy);
                await command.ExecuteNonQueryAsync();
            }
        }
    }

    private sealed class InjectedDbException(string message) : DbException(message);
}
