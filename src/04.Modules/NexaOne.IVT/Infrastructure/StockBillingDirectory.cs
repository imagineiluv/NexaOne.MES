using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using NexaOne.ServiceContracts.Ivt;

namespace NexaOne.IVT.Infrastructure;

/// <summary>Inventory-owned, transaction-confined billing projection. It exposes issue identity and
/// quantity only; customer and pricing remain ERP-owned.</summary>
public sealed class StockBillingDirectory : IStockBillingDirectory
{
    private const int MaxBatchSize = 128;

    public async Task<StockBillingOccurrence?> GetMovementInTransactionAsync(DbTransaction transaction,
        Guid tenantId, Guid organizationId, Guid movementId, CancellationToken ct = default)
    {
        var rows = await FindMovementsInTransactionAsync(transaction, tenantId, organizationId,
            [movementId], ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<StockBillingOccurrence>> FindMovementsInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId,
        IReadOnlyList<Guid> movementIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(movementIds);
        if (transaction.Connection is null || tenantId == Guid.Empty || organizationId == Guid.Empty
            || movementIds.Count is < 1 or > MaxBatchSize || movementIds.Any(id => id == Guid.Empty)
            || movementIds.Distinct().Count() != movementIds.Count)
            throw new InvalidDataException("Invalid stock billing directory request.");
        var command = new CommandDefinition("""
            SELECT M.MOVEMENT_ID AS MovementId, P.PRODUCT_ID AS ProductId,
                   M.VARIANT_ID AS VariantId, M.QUANTITY AS Quantity,
                   M.KIND AS Kind, M.REVERSED_BY_ID AS ReversedBy, M.AT_TICKS AS AtTicks
              FROM IVT_STOCK_MOVEMENT M
              JOIN IVT_STOCK_PRODUCT P
                ON P.TENANT_ID=M.TENANT_ID AND P.ORGANIZATION_ID=M.ORGANIZATION_ID
               AND P.VARIANT_ID=M.VARIANT_ID
             WHERE M.TENANT_ID=@Tenant AND M.ORGANIZATION_ID=@Organization
               AND M.MOVEMENT_ID IN @Movements
             ORDER BY M.MOVEMENT_ID
            """, new { Tenant = Text(tenantId), Organization = Text(organizationId),
                Movements = movementIds.Select(Text).ToArray() }, transaction,
            cancellationToken: ct);
        var rows = (await transaction.Connection.QueryAsync<Row>(command)).ToArray();
        if (rows.Length > movementIds.Count) throw new InvalidDataException("Duplicate stock billing rows.");
        var expected = movementIds.Select(Text).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new StockBillingOccurrence[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var row = rows[index];
            if (!expected.Contains(row.MovementId) || !seen.Add(row.MovementId))
                throw new InvalidDataException("Unexpected stock billing row.");
            var quantity = Quantity(row.Quantity);
            result[index] = new(Id(row.MovementId), Id(row.ProductId), Id(row.VariantId),
                DateOnly.FromDateTime(new DateTime(row.AtTicks, DateTimeKind.Utc)), quantity,
                row.Kind == 1, row.ReversedBy is not null);
        }
        return Array.AsReadOnly(result);
    }

    private static string Text(Guid value) => value.ToString("D");
    private static Guid Id(string value)
        => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && Text(id) == value
            ? id : throw new InvalidDataException("Stock billing storage contains an invalid identity.");
    private static decimal Quantity(string value)
        => decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
            out var quantity) && quantity > 0m && quantity.ToString("0.######", CultureInfo.InvariantCulture) == value
            ? quantity : throw new InvalidDataException("Stock billing storage contains an invalid quantity.");

    private sealed class Row
    {
        public string MovementId { get; set; } = "";
        public string ProductId { get; set; } = "";
        public string VariantId { get; set; } = "";
        public string Quantity { get; set; } = "";
        public int Kind { get; set; }
        public string? ReversedBy { get; set; }
        public long AtTicks { get; set; }
    }
}
