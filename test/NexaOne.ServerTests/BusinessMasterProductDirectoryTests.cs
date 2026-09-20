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
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _directory.FindProductsAsync(transactionObject, ["OWNER-P1"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _directory.FindPlantDetailsAsync(transactionObject, "OWNER-PLANT"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _directory.QueryActiveWorkersAsync(transactionObject, "OWNER-PLANT"));

        connection.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());
        transaction.Protected().Verify("Dispose", Times.Never(), ItExpr.IsAny<bool>());
    }

    [Fact]
    public async Task Product_lookup_rejects_completed_and_missing_transactions()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _directory.FindProductAsync(null!, "OWNER-P1"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _directory.FindProductsAsync(null!, ["OWNER-P1"]));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _directory.FindPlantDetailsAsync(null!, "OWNER-PLANT"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _directory.QueryActiveWorkersAsync(null!, "OWNER-PLANT"));
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        transaction.Rollback();

        await Assert.ThrowsAsync<InvalidOperationException>(() => _directory.FindProductAsync(transaction, "OWNER-P1"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _directory.FindProductsAsync(transaction, ["OWNER-P1"]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _directory.FindPlantDetailsAsync(transaction, "OWNER-PLANT"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT"));
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
        var batchError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _directory.FindProductsAsync(transactionObject, ["OWNER-P1"], cancellation.Token));
        batchError.CancellationToken.Should().Be(cancellation.Token);
        var plantError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _directory.FindPlantDetailsAsync(transactionObject, "OWNER-PLANT", cancellation.Token));
        plantError.CancellationToken.Should().Be(cancellation.Token);
        var workerError = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _directory.QueryActiveWorkersAsync(transactionObject, "OWNER-PLANT", ct: cancellation.Token));
        workerError.CancellationToken.Should().Be(cancellation.Token);
        transaction.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Product_batch_reads_only_requested_live_masters_and_preserves_caller_rollback()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            await connection.ExecuteAsync("""
                INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, DESCRIPTION, PRODUCT_TYPE, UNIT, VALID_STATE)
                VALUES ('OWNER-BATCH-A', 'First product', 'Description', 'Material', 'kg', 'Valid'),
                       ('OWNER-BATCH-B', 'Second product', NULL, 'Material', 'EA', 'Invalid'),
                       ('OWNER-BATCH-HIDDEN', 'Unrequested product', NULL, 'Material', 'EA', 'Valid')
                """, transaction: transaction);
            var first = await _directory.FindProductsAsync(transaction,
                ["OWNER-BATCH-B", "OWNER-BATCH-MISSING", "OWNER-BATCH-A"]);
            first.Should().Equal(
                new ProductDto("OWNER-BATCH-A", "First product", "Description", "Material", "kg", "Valid"),
                new ProductDto("OWNER-BATCH-B", "Second product", "", "Material", "EA", "Invalid"));
            Action mutation = () => ((IList<ProductDto>)first)[0] = first[1];
            mutation.Should().Throw<NotSupportedException>();

            await connection.ExecuteAsync("""
                UPDATE MDM_PRODUCT SET PRODUCT_NAME='Changed product', UNIT='g', VALID_STATE='Invalid'
                WHERE PRODUCT_ID='OWNER-BATCH-A'
                """, transaction: transaction);
            (await _directory.FindProductsAsync(transaction, ["OWNER-BATCH-A"])).Should().Equal(
                new ProductDto("OWNER-BATCH-A", "Changed product", "Description", "Material", "g", "Invalid"));
            first[0].ProductName.Should().Be("First product");
            transaction.Connection.Should().BeSameAs(connection);
            transaction.Rollback();
        }
        connection.State.Should().Be(ConnectionState.Open);
        (await connection.ExecuteScalarAsync<long>(
            "SELECT COUNT(*) FROM MDM_PRODUCT WHERE PRODUCT_ID IN ('OWNER-BATCH-A','OWNER-BATCH-B','OWNER-BATCH-HIDDEN')")).Should().Be(0);
    }

    [Fact]
    public async Task Product_batch_accepts_128_keys_and_treats_wildcards_and_quotes_as_literal_keys()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        var keys = Enumerable.Range(0, 127).Select(index => $"OWNER-BATCH-{index:D3}")
            .Append("OWNER-%_[~'").ToArray();
        await connection.ExecuteAsync("""
            INSERT INTO MDM_PRODUCT (PRODUCT_ID, PRODUCT_NAME, PRODUCT_TYPE, UNIT, VALID_STATE)
            VALUES (@Id, 'Batch product', 'Material', 'EA', 'Valid')
            """, keys.Select(key => new { Id = key }), transaction);

        var result = await _directory.FindProductsAsync(transaction, keys);

        result.Should().HaveCount(128);
        result.Select(product => product.ProductId).Should().BeEquivalentTo(keys);
        (await _directory.FindProductsAsync(transaction, ["OWNER-%_[~'"])).Should().ContainSingle()
            .Which.ProductId.Should().Be("OWNER-%_[~'");
        transaction.Rollback();
    }

    [Fact]
    public async Task Product_batch_rejects_noncanonical_duplicate_and_oversized_key_sets()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        (await _directory.FindProductsAsync(transaction, [])).Should().BeEmpty();
        await Assert.ThrowsAsync<ArgumentNullException>(() => _directory.FindProductsAsync(transaction, null!));
        foreach (var keys in new string[][] { [""], [" "], [" OWNER-P1"], ["OWNER-P1 "],
            [new string('P', 51)], [null!], ["OWNER-P1", "OWNER-P1"] })
            await Assert.ThrowsAsync<ArgumentException>(() => _directory.FindProductsAsync(transaction, keys));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _directory.FindProductsAsync(transaction,
            Enumerable.Range(0, 129).Select(index => $"OWNER-{index}").ToArray()));
        transaction.Connection.Should().BeSameAs(connection);
        transaction.Rollback();
    }

    [Fact]
    public async Task Plant_details_preserve_live_canonical_metadata_and_caller_rollback()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            await connection.ExecuteAsync("""
                INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME, DESCRIPTION, COUNTRY, TIME_ZONE)
                VALUES ('OWNER-PLANT', 'Original plant', 'Plant description', 'KR', 'Asia/Seoul')
                """, transaction: transaction);
            var writes = await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction);
            var original = await _directory.FindPlantDetailsAsync(transaction, "OWNER-PLANT");
            original.Should().Be(new PlantDto("OWNER-PLANT", "Original plant", "Plant description", "KR", "Asia/Seoul"));
            foreach (var key in new[] { null!, "", " ", " OWNER-PLANT", "OWNER-PLANT ", new string('P', 51), "missing" })
                (await _directory.FindPlantDetailsAsync(transaction, key)).Should().BeNull();
            (await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction)).Should().Be(writes);
            await connection.ExecuteAsync("""
                UPDATE MDM_PLANT SET PLANT_NAME='Changed plant', DESCRIPTION=NULL, COUNTRY=NULL, TIME_ZONE=NULL
                WHERE PLANT_ID='OWNER-PLANT'
                """, transaction: transaction);
            (await _directory.FindPlantDetailsAsync(transaction, "OWNER-PLANT")).Should().Be(
                new PlantDto("OWNER-PLANT", "Changed plant", "", "", ""));
            original!.PlantName.Should().Be("Original plant");
            transaction.Connection.Should().BeSameAs(connection);
            transaction.Rollback();
        }
        connection.State.Should().Be(ConnectionState.Open);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM MDM_PLANT WHERE PLANT_ID='OWNER-PLANT'")).Should().Be(0);
    }

    [Fact]
    public async Task Worker_query_filters_live_transfers_and_activity_across_scan_batches_before_paging_without_writes()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using (var transaction = connection.BeginTransaction(IsolationLevel.Serializable))
        {
            await connection.ExecuteAsync("""
                INSERT INTO MDM_PLANT (PLANT_ID, PLANT_NAME) VALUES ('OWNER-PLANT', 'First'), ('OWNER-OTHER', 'Second');
                """, transaction: transaction);
            await connection.ExecuteAsync("""
                INSERT INTO MDM_WORKER (WORKER_ID, WORKER_NAME, PLANT_ID, IS_ACTIVE)
                VALUES (@Id, 'Other worker', 'OWNER-PLANT', 1)
                """, Enumerable.Range(0, 300).Select(index => new { Id = $"OWNER-W{index:D3}" }), transaction);
            await connection.ExecuteAsync("""
                UPDATE MDM_WORKER SET WORKER_NAME='ÄLPHA %_[x]'
                WHERE WORKER_ID IN ('OWNER-W000','OWNER-W127','OWNER-W128','OWNER-W255','OWNER-W256','OWNER-W299');
                """, transaction: transaction);
            var before = await _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", "älpha %_[X]");
            before.Total.Should().Be(6);
            before.Items.Should().HaveCount(6).And.OnlyContain(worker => worker.IsActive && worker.PlantId == "OWNER-PLANT");
            await connection.ExecuteAsync("""
                UPDATE MDM_WORKER SET IS_ACTIVE=0 WHERE WORKER_ID='OWNER-W128';
                UPDATE MDM_WORKER SET PLANT_ID='OWNER-OTHER' WHERE WORKER_ID='OWNER-W255';
                UPDATE MDM_WORKER SET WORKER_NAME='ÄLPHA something-x' WHERE WORKER_ID='OWNER-W001';
                INSERT INTO MDM_WORKER (WORKER_ID, WORKER_NAME, PLANT_ID, IS_ACTIVE)
                VALUES ('OWNER-ORPHAN', 'ÄLPHA %_[x]', 'OWNER-MISSING', 1);
                """, transaction: transaction);
            var writes = await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction);

            var page = await _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", "älpha %_[X]", 1, 2);
            page.Total.Should().Be(4);
            page.Items.Should().Equal(new WorkerDto("OWNER-W127", "ÄLPHA %_[x]", "OWNER-PLANT", true),
                new WorkerDto("OWNER-W256", "ÄLPHA %_[x]", "OWNER-PLANT", true));
            ((IList<WorkerDto>)page.Items).IsReadOnly.Should().BeTrue();
            var past = await _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", "älpha %_[X]", int.MaxValue, 100);
            past.Total.Should().Be(4); past.Items.Should().BeEmpty();
            var unfiltered = await _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", limit: 100);
            unfiltered.Total.Should().Be(298); unfiltered.Items.Should().HaveCount(100);
            (await _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", "owner-w299")).Items.Should().ContainSingle();
            (await _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", "  ")).Total.Should().Be(0);
            (await _directory.QueryActiveWorkersAsync(transaction, "OWNER-MISSING")).Total.Should().Be(0);
            (await _directory.QueryActiveWorkersAsync(transaction, "OWNER-OTHER")).Items.Should().ContainSingle()
                .Which.WorkerId.Should().Be("OWNER-W255");
            before.Items.Single(worker => worker.WorkerId == "OWNER-W128").IsActive.Should().BeTrue();
            (await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction)).Should().Be(writes);
            transaction.Connection.Should().BeSameAs(connection);
            transaction.Rollback();
        }
        connection.State.Should().Be(ConnectionState.Open);
        (await connection.ExecuteScalarAsync<long>("SELECT COUNT(*) FROM MDM_WORKER WHERE WORKER_ID LIKE 'OWNER-%'")).Should().Be(0);
    }

    [Fact]
    public async Task Worker_query_validates_keys_text_and_page_bounds_without_trimming_literal_text()
    {
        using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction(IsolationLevel.Serializable);
        foreach (var key in new[] { null!, "", " ", " OWNER-PLANT", "OWNER-PLANT ", new string('P', 51) })
            await Assert.ThrowsAsync<ArgumentException>(() => _directory.QueryActiveWorkersAsync(transaction, key));
        await Assert.ThrowsAsync<ArgumentException>(() => _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", new string('x', 257)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", offset: -1));
        foreach (var limit in new[] { -1, 0, 101 })
            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _directory.QueryActiveWorkersAsync(transaction, "OWNER-PLANT", limit: limit));
        foreach (var text in new[] { null, "", " ", new string('x', 256) })
        {
            var page = await _directory.QueryActiveWorkersAsync(transaction, new string('P', 50), text, limit: 1);
            page.Total.Should().Be(0); page.Items.Should().BeEmpty();
        }
        (await connection.ExecuteScalarAsync<long>("SELECT total_changes()", transaction: transaction)).Should().Be(0);
        transaction.Rollback();
    }
}
