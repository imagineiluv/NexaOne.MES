using System.Data;
using System.Data.Common;
using Dapper;
using NexaDB.Data.Abstractions.Models;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Infrastructure;

/// <summary>Reads MDM master identity and eligibility without leaving the caller's transaction.</summary>
public sealed class BusinessMasterDirectory : IBusinessMasterDirectory
{
    private readonly int? _timeout;
    private readonly string _batchLimitSql;
    private const string ProductColumns = "PRODUCT_ID AS ProductId, PRODUCT_NAME AS ProductName, "
        + "COALESCE(DESCRIPTION, '') AS Description, PRODUCT_TYPE AS ProductType, UNIT AS Unit, VALID_STATE AS ValidState";

    public BusinessMasterDirectory(EesDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        if (_timeout is <= 0)
            throw new ArgumentOutOfRangeException(nameof(dataSource), "Command timeout must be greater than zero seconds.");
        _batchLimitSql = dataSource.Provider?.Kind == DatabaseProviderKind.SqlServer
            ? " OFFSET 0 ROWS FETCH NEXT @BatchSize ROWS ONLY" : " LIMIT @BatchSize";
    }

    public async Task<string?> FindPlantAsync(
        DbTransaction transaction, string plantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(plantId)) return null;
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT PLANT_ID FROM MDM_PLANT WHERE PLANT_ID=@plantId",
            new { plantId }, transaction, commandTimeout: _timeout, cancellationToken: ct));
    }

    public async Task<string?> FindActiveWorkerAsync(
        DbTransaction transaction, string workerId, string plantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(workerId) || !ValidKey(plantId)) return null;
        return await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
            "SELECT WORKER_ID FROM MDM_WORKER WHERE WORKER_ID=@workerId AND PLANT_ID=@plantId AND IS_ACTIVE=1",
            new { workerId, plantId }, transaction, commandTimeout: _timeout, cancellationToken: ct));
    }

    public async Task<PlantDto?> FindPlantDetailsAsync(
        DbTransaction transaction, string plantId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(plantId)) return null;
        return await connection.QuerySingleOrDefaultAsync<PlantDto>(new CommandDefinition(
            """
            SELECT PLANT_ID AS PlantId, PLANT_NAME AS PlantName, COALESCE(DESCRIPTION, '') AS Description,
                   COALESCE(COUNTRY, '') AS Country, COALESCE(TIME_ZONE, '') AS TimeZone
              FROM MDM_PLANT WHERE PLANT_ID=@plantId
            """, new { plantId }, transaction, commandTimeout: _timeout, cancellationToken: ct));
    }

    public async Task<(IReadOnlyList<WorkerDto> Items, long Total)> QueryActiveWorkersAsync(
        DbTransaction transaction, string plantId, string? text = null, int offset = 0, int limit = 50,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(plantId)) throw new ArgumentException("A canonical plant ID is required.", nameof(plantId));
        if (text?.Length > 256) throw new ArgumentException("Text must not exceed 256 characters.", nameof(text));
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 100.");
        // O(plant workers): bounded keyset batches and managed matching avoid provider collation
        // and LIKE wildcard differences. Only matching page rows survive this transaction.
        const int batchSize = 128;
        var items = new List<WorkerDto>(limit);
        long total = 0;
        var end = (long)offset + limit;
        string? afterWorkerId = null;
        while (true)
        {
            var rows = (await connection.QueryAsync<(string WorkerId, string WorkerName, string PlantId)>(new CommandDefinition("""
                SELECT w.WORKER_ID, w.WORKER_NAME, p.PLANT_ID
                  FROM MDM_WORKER w JOIN MDM_PLANT p ON p.PLANT_ID=w.PLANT_ID
                 WHERE p.PLANT_ID=@plantId AND w.IS_ACTIVE=1 AND (@afterWorkerId IS NULL OR w.WORKER_ID>@afterWorkerId)
                 ORDER BY w.WORKER_ID
                """ + _batchLimitSql, new { plantId, afterWorkerId, BatchSize = batchSize }, transaction,
                commandTimeout: _timeout, cancellationToken: ct))).ToArray();
            foreach (var worker in rows)
            {
                ct.ThrowIfCancellationRequested();
                if (!string.IsNullOrEmpty(text) && !worker.WorkerId.Contains(text, StringComparison.OrdinalIgnoreCase)
                    && !worker.WorkerName.Contains(text, StringComparison.OrdinalIgnoreCase)) continue;
                // The SQL predicate guarantees activity; avoid provider-specific BIT/INTEGER constructor binding.
                if (total >= offset && total < end) items.Add(new(worker.WorkerId, worker.WorkerName, worker.PlantId, true));
                total++;
            }
            if (rows.Length < batchSize) break;
            afterWorkerId = rows[^1].WorkerId;
        }
        ct.ThrowIfCancellationRequested();
        return (Array.AsReadOnly(items.ToArray()), total);
    }

    public async Task<ProductDto?> FindProductAsync(
        DbTransaction transaction, string productId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(productId)) return null;
        return await connection.QuerySingleOrDefaultAsync<ProductDto>(new CommandDefinition(
            "SELECT " + ProductColumns + " FROM MDM_PRODUCT WHERE PRODUCT_ID=@productId",
            new { productId }, transaction, commandTimeout: _timeout, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ProductDto>> FindProductsAsync(
        DbTransaction transaction, IReadOnlyList<string> productIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        ArgumentNullException.ThrowIfNull(productIds);
        if (productIds.Count > 128) throw new ArgumentOutOfRangeException(nameof(productIds), "At most 128 product IDs are allowed.");
        var ids = productIds.ToArray();
        if (ids.Any(id => !ValidKey(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Product IDs must be distinct canonical keys.", nameof(productIds));
        if (ids.Length == 0) return Array.Empty<ProductDto>();
        var rows = await connection.QueryAsync<ProductDto>(new CommandDefinition(
            "SELECT " + ProductColumns + " FROM MDM_PRODUCT WHERE PRODUCT_ID IN @ids ORDER BY PRODUCT_ID",
            new { ids }, transaction, commandTimeout: _timeout, cancellationToken: ct));
        return Array.AsReadOnly(rows.ToArray());
    }

    // Activity is read as an integer expression: SQLite returns BIT as INTEGER, so a bool record parameter cannot bind.
    private const string CustomerColumns = "CUSTOMER_ID, CUSTOMER_NAME, CASE WHEN IS_ACTIVE=1 THEN 1 ELSE 0 END";
    private static CustomerDto Customer((string Id, string Name, long Active) row) => new(row.Id, row.Name, row.Active == 1);

    public async Task<CustomerDto?> FindCustomerAsync(
        DbTransaction transaction, string customerId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(customerId)) return null;
        var rows = await connection.QueryAsync<(string Id, string Name, long Active)>(new CommandDefinition(
            "SELECT " + CustomerColumns + " FROM MDM_CUSTOMER WHERE CUSTOMER_ID=@customerId",
            new { customerId }, transaction, commandTimeout: _timeout, cancellationToken: ct));
        var row = rows.SingleOrDefault();
        return row.Id is null ? null : Customer(row);
    }

    public async Task<IReadOnlyList<CustomerDto>> FindCustomersAsync(
        DbTransaction transaction, IReadOnlyList<string> customerIds, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        ArgumentNullException.ThrowIfNull(customerIds);
        if (customerIds.Count > 128) throw new ArgumentOutOfRangeException(nameof(customerIds), "At most 128 customer IDs are allowed.");
        var ids = customerIds.ToArray();
        if (ids.Any(id => !ValidKey(id)) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new ArgumentException("Customer IDs must be distinct canonical keys.", nameof(customerIds));
        if (ids.Length == 0) return Array.Empty<CustomerDto>();
        var rows = await connection.QueryAsync<(string Id, string Name, long Active)>(new CommandDefinition(
            "SELECT " + CustomerColumns + " FROM MDM_CUSTOMER WHERE CUSTOMER_ID IN @ids ORDER BY CUSTOMER_ID",
            new { ids }, transaction, commandTimeout: _timeout, cancellationToken: ct));
        return Array.AsReadOnly(rows.Select(Customer).ToArray());
    }

    private static DbConnection RequireSerializableConnection(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection;
        if (connection is null || connection.State != ConnectionState.Open
            || transaction.IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("A live Serializable transaction is required.");
        return connection;
    }

    private static bool ValidKey(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 50 && value == value.Trim();
}
