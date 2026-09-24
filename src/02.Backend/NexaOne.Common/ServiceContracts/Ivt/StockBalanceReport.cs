using NexaFramework.Service;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>A point-in-time view of recorded stock balances. Untouched warehouse/variant pairs are omitted.</summary>
public sealed record StockBalanceReport(
    BusinessScope Scope,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<StockBalanceReportRow> Rows);

public sealed record StockBalanceReportRow(
    Guid WarehouseId,
    string WarehouseCode,
    string WarehouseName,
    bool WarehouseActive,
    Guid VariantId,
    string ProductId,
    string ProductName,
    bool ProductActive,
    string Unit,
    decimal OnHand,
    decimal Reserved,
    decimal Available);

public sealed record StockBalanceCsvExport(string FileName, string ContentType, byte[] Content);
