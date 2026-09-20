using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Infrastructure;

/// <summary>Reads MDM master identity and eligibility without leaving the caller's transaction.</summary>
public sealed class BusinessMasterDirectory : IBusinessMasterDirectory
{
    private readonly int? _timeout;

    public BusinessMasterDirectory(EesDataSource dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        if (_timeout is <= 0)
            throw new ArgumentOutOfRangeException(nameof(dataSource), "Command timeout must be greater than zero seconds.");
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

    public async Task<ProductDto?> FindProductAsync(
        DbTransaction transaction, string productId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(productId)) return null;
        return await connection.QuerySingleOrDefaultAsync<ProductDto>(new CommandDefinition(
            """
            SELECT PRODUCT_ID AS ProductId, PRODUCT_NAME AS ProductName,
                   COALESCE(DESCRIPTION, '') AS Description, PRODUCT_TYPE AS ProductType,
                   UNIT AS Unit, VALID_STATE AS ValidState
              FROM MDM_PRODUCT WHERE PRODUCT_ID=@productId
            """, new { productId }, transaction, commandTimeout: _timeout, cancellationToken: ct));
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
