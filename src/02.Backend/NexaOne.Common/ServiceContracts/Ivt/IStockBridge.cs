using NexaFramework.Service;
using NexaFramework.Service.Inventory;

namespace NexaOne.ServiceContracts.Ivt;

/// <summary>Scoped business stock, separate from manufacturing LOT balances. Every operation checks
/// current SYS authority and the explicit IVT plant binding in its owning database transaction.</summary>
public interface IStockBridge : INexaModuleBridge
{
    /// <summary>Atomically imports product enrollments and warehouses after validating every row.
    /// A successful operation can be replayed with the same actor and payload without writing again.</summary>
    Task<StockMasterImportResult> ImportMastersAsync(string userId, Guid tenantId, Guid organizationId,
        StockMasterImportRequest request, CancellationToken ct = default);
    /// <summary>Enrolls an existing MDM product with one stable base variant and a frozen unit.
    /// Requires stock.product.write. Repeating the same product/unit does not create another mapping.</summary>
    Task<ProductVariant> EnrollProductAsync(string userId, Guid tenantId, Guid organizationId,
        string productId, string unit, CancellationToken ct = default);
    Task<ProductVariant> GetProductAsync(string userId, Guid tenantId, Guid organizationId,
        string productId, CancellationToken ct = default);
    /// <summary>Lists enrolled products using current MDM names and active state before filtering and paging.
    /// Product activity does not imply the frozen variant unit is still eligible; GetProductAsync reads that variant.</summary>
    Task<BusinessPage<Product>> ListProductsAsync(string userId, Guid tenantId, Guid organizationId,
        InventoryQuery query, CancellationToken ct = default);
    /// <summary>Creates once per actor/operation/payload; replay survives later warehouse renaming.</summary>
    Task<Warehouse> CreateWarehouseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, string code, string name, CancellationToken ct = default);
    Task<Warehouse> UpdateWarehouseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, string code, string name, CancellationToken ct = default);
    Task<Warehouse> GetWarehouseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    /// <summary>Lists warehouses and the matching total under the caller's current scoped read grant.</summary>
    Task<BusinessPage<Warehouse>> ListWarehousesAsync(string userId, Guid tenantId, Guid organizationId,
        InventoryQuery query, CancellationToken ct = default);
    Task<Warehouse> SetWarehouseActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default);
    Task<StockBalance?> GetBalanceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid variantId, Guid warehouseId, CancellationToken ct = default);
    /// <summary>Uses the Framework's scope-wide operation replay contract; current permission is required on every retry.</summary>
    Task<StockMovement> PostAsync(string userId, Guid tenantId, Guid organizationId,
        StockPosting posting, CancellationToken ct = default);
    Task<StockMovement> GetMovementAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<BusinessPage<StockMovement>> ListMovementsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid variantId, int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<StockMovement> ReverseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, Guid operationId, string reference, CancellationToken ct = default);
    Task<StockReservation> ReserveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid variantId, Guid warehouseId, decimal quantity, string reference, CancellationToken ct = default);
    Task<StockReservation> GetReservationAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default);
    Task<ProductVariant> GetVariantAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default);
    Task<BusinessPage<StockBalance>> ListBalancesAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? warehouseId = null, Guid? variantId = null, int offset = 0, int limit = 50, CancellationToken ct = default);
    /// <summary>Builds a stable, current snapshot of recorded balances. The report fails rather than truncates
    /// when its bounded export size is exceeded. Requires stock.read.</summary>
    Task<StockBalanceReport> BuildBalanceReportAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? warehouseId = null, Guid? variantId = null, CancellationToken ct = default);
    Task<StockBalanceCsvExport> ExportBalanceReportCsvAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? warehouseId = null, Guid? variantId = null, CancellationToken ct = default);
    Task<BusinessPage<StockReservation>> ListReservationsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? variantId = null, Guid? warehouseId = null, StockReservationState? state = null,
        int offset = 0, int limit = 50, CancellationToken ct = default);
    Task<StockReservation> ReleaseReservationAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
    Task<StockMovement> ConsumeReservationAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default);
}

public sealed record StockMasterImportRequest(Guid OperationId,
    IReadOnlyList<StockProductImportRow> Products, IReadOnlyList<StockWarehouseImportRow> Warehouses);

public sealed record StockProductImportRow(string ProductId, string Unit);

public sealed record StockWarehouseImportRow(string Code, string Name);

public sealed record StockMasterImportError(string Section, int Row, string Field, string Code);

public sealed record StockMasterImportResult(Guid OperationId, bool Applied, bool Replayed,
    int ProductsCreated, int ProductsUnchanged, int WarehousesCreated, int WarehousesUnchanged,
    IReadOnlyList<StockMasterImportError> Errors);
