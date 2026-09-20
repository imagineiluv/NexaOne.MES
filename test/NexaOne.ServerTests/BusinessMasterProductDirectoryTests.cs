using System.Data;
using System.Data.Common;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using Moq.Protected;
using NexaOne.Infrastructure.Persistence;
using NexaOne.MDM.Infrastructure;
using NexaOne.ServiceContracts.Mdm;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class BusinessMasterProductDirectoryTests : IClassFixture<BusinessMembershipDatabaseTemplate>, IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;
    // The owner must use the caller's connection; this data source supplies only command options.
    private readonly BusinessMasterDirectory _directory = new(new EesDataSource());

    public BusinessMasterProductDirectoryTests(BusinessMembershipDatabaseTemplate template)
    {
        _path = template.Copy();
        _connectionString = $"Data Source={_path};Foreign Keys=True;Pooling=False;Default Timeout=10";
    }

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task Product_lookup_reads_live_master_fields_in_the_callers_transaction_without_committing()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            await connection.ExecuteAsync("""
                INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, DESCRIPTION, PRODUCT_TYPE, UNIT, VALID_STATE)
                VALUES ('OWNER-P1', 'Original product', 'Master description', 'Material', 'kg', 'Valid')
                """, transaction: transaction);
            (await _directory.FindProductAsync(transaction, "OWNER-P1")).Should().Be(
                new ProductDto("OWNER-P1", "Original product", "Master description", "Material", "kg", "Valid"));

            await connection.ExecuteAsync("""
                UPDATE MDM_PRODUCT SET PRODUCT_NAME='Changed product', DESCRIPTION=NULL, UNIT='g', VALID_STATE='Invalid'
                 WHERE PRODUCT_ID='OWNER-P1'
                """, transaction: transaction);
            (await _directory.FindProductAsync(transaction, "OWNER-P1")).Should().Be(
                new ProductDto("OWNER-P1", "Changed product", "", "Material", "g", "Invalid"));
            transaction.Connection.Should().BeSameAs(connection);
            transaction.Rollback();
        }

        connection.State.Should().Be(ConnectionState.Open);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM MDM_PRODUCT WHERE PRODUCT_ID='OWNER-P1'")).Should().Be(0);
    }

    [Fact]
    public async Task Product_lookup_returns_null_for_missing_and_noncanonical_keys()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var longestId = new string('P', 50);
        await connection.ExecuteAsync("""
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT)
            VALUES (@longestId, 'Boundary product', 'Material', 'EA'),
                   ('OWNER-P1', 'Short key product', 'Material', 'EA')
            """, new { longestId }, transaction);

        (await _directory.FindProductAsync(transaction, longestId))!.ProductId.Should().Be(longestId);
        foreach (var key in new[] { "", " ", "missing-product", longestId + "P", " OWNER-P1", "OWNER-P1 " })
            (await _directory.FindProductAsync(transaction, key)).Should().BeNull();
        (await _directory.FindProductAsync(transaction, null!)).Should().BeNull();
        transaction.Rollback();
    }

    [Theory]
    [InlineData(ConnectionState.Open, IsolationLevel.ReadCommitted)]
    [InlineData(ConnectionState.Open, IsolationLevel.RepeatableRead)]
    [InlineData(ConnectionState.Open, IsolationLevel.Snapshot)]
    [InlineData(ConnectionState.Closed, IsolationLevel.Serializable)]
    [InlineData(ConnectionState.Broken, IsolationLevel.Serializable)]
    public async Task Product_lookup_rejects_nonserializable_or_nonopen_transactions(
        ConnectionState state, IsolationLevel isolation)
    {
        var connection = new Mock<DbConnection>(MockBehavior.Strict);
        // Component's finalizer invokes Dispose(false); keep cleanup safe without relaxing database calls.
        connection.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        using var connectionObject = connection.Object;
        connection.SetupGet(value => value.State).Returns(state);
        var transaction = new Mock<DbTransaction>(MockBehavior.Strict);
        transaction.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        using var transactionObject = transaction.Object;
        transaction.Protected().SetupGet<DbConnection>("DbConnection").Returns(connectionObject);
        transaction.SetupGet(value => value.IsolationLevel).Returns(isolation);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _directory.FindProductAsync(transactionObject, "OWNER-P1"));

        connection.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());
        transaction.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());
    }

    [Fact]
    public async Task Product_lookup_rejects_completed_and_missing_transactions()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _directory.FindProductAsync(null!, "OWNER-P1"));
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        transaction.Rollback();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _directory.FindProductAsync(transaction, "OWNER-P1"));
    }

    [Fact]
    public async Task Product_lookup_honors_cancellation_before_accessing_the_transaction()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var transaction = new Mock<DbTransaction>(MockBehavior.Strict);
        transaction.Protected().Setup("Dispose", ItExpr.IsAny<bool>());
        using var transactionObject = transaction.Object;

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _directory.FindProductAsync(transactionObject, "OWNER-P1", cancellation.Token));

        error.CancellationToken.Should().Be(cancellation.Token);
        transaction.VerifyNoOtherCalls();
    }
}
