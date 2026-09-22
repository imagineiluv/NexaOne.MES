using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Dapper;
using NexaDB.Data.Abstractions.Models;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.IVT.Infrastructure;

/// <summary>Owns business stock persistence and explicit product enrollment. All owner reads,
/// balance changes, immutable ledger entries, reservations and audit share one Serializable commit.</summary>
public sealed class StockBridge : IStockBridge
{
    private const string ScopeWhere = "TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId";
    private readonly ServiceObjectProcessor _processor;
    private readonly int? _timeout;
    private readonly TimeProvider _clock;
    private readonly IBusinessMembershipBridge _memberships;
    private readonly IBusinessMasterDirectory _masters;
    private readonly string _batchLimitSql;

    public StockBridge(EesDataSource dataSource, IBusinessMembershipBridge memberships,
        IBusinessMasterDirectory masters, TimeProvider? clock = null)
    {
        _processor = new(dataSource);
        _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        _clock = clock ?? TimeProvider.System;
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _masters = masters ?? throw new ArgumentNullException(nameof(masters));
        _batchLimitSql = dataSource.Provider?.Kind == DatabaseProviderKind.SqlServer
            ? " OFFSET 0 ROWS FETCH NEXT @BatchSize ROWS ONLY" : " LIMIT @BatchSize";
    }

    public Task<ProductVariant> EnrollProductAsync(string userId, Guid tenantId, Guid organizationId,
        string productId, string unit, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.product.write", (_, session) => session.Enroll(productId, unit, ct), ct);
    public Task<ProductVariant> GetProductAsync(string userId, Guid tenantId, Guid organizationId,
        string productId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.read", async (_, session) =>
        {
            RequireText(productId, 50);
            var master = await session.Master(productId, ct) ?? throw Failure("PRODUCT_NOT_FOUND");
            var row = await session.ProductRow("MASTER_PRODUCT_ID=@key", master.ProductId, ct)
                ?? throw Failure("PRODUCT_NOT_ENROLLED");
            return await session.Variant(row, ct);
        }, ct);
    public Task<Warehouse> CreateWarehouseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, string code, string name, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.warehouse.write", async (service, session) =>
        {
            if (operationId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var prior = await session.Creation(operationId, ct);
            if (prior is not null)
            {
                if (prior.Actor != session.Actor.UserId || prior.Code != code || prior.Name != name)
                    throw Failure("WAREHOUSE_CREATE_OPERATION_CONFLICT");
                return await session.FindWarehouseAsync(Id(prior.Id), ct) ?? throw Failure("WAREHOUSE_NOT_FOUND");
            }
            var value = await service.SaveWarehouseAsync(session.Actor, code, name, ct: ct);
            await session.RecordCreation(operationId, value, ct);
            return value;
        }, ct);
    public Task<Warehouse> UpdateWarehouseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, string code, string name, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.warehouse.write",
            (service, session) => service.SaveWarehouseAsync(session.Actor, code, name, id, version, ct), ct);
    public Task<Warehouse> GetWarehouseAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.warehouse.read", (service, session) => service.GetWarehouseAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<Warehouse>> ListWarehousesAsync(string userId, Guid tenantId, Guid organizationId,
        InventoryQuery query, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.warehouse.read", (service, session) => service.ListWarehousesAsync(session.Actor, query, ct), ct);
    public Task<BusinessPage<Product>> ListProductsAsync(string userId, Guid tenantId, Guid organizationId,
        InventoryQuery query, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.read", (service, session) => service.ListProductsAsync(session.Actor, query, ct), ct);
    public Task<Warehouse> SetWarehouseActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.warehouse.write", (service, session) => service.SetWarehouseActiveAsync(session.Actor, id, version, active, ct), ct);
    public Task<StockBalance?> GetBalanceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid variantId, Guid warehouseId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.read", (service, session) => service.GetBalanceAsync(session.Actor, variantId, warehouseId, ct), ct);
    public Task<StockMovement> PostAsync(string userId, Guid tenantId, Guid organizationId, StockPosting posting, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.post", (service, session) => service.PostAsync(session.Actor, posting, ct), ct);
    public Task<StockMovement> GetMovementAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.read", (service, session) => service.GetMovementAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<StockMovement>> ListMovementsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid variantId, int offset = 0, int limit = 50, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.read", (service, session) => service.ListMovementsAsync(session.Actor, variantId, offset, limit, ct), ct);
    public Task<BusinessPage<StockReservation>> ListReservationsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? variantId = null, Guid? warehouseId = null, StockReservationState? state = null, int offset = 0, int limit = 50, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.read", (service, session) => service.ListReservationsAsync(session.Actor, variantId, warehouseId, state, offset, limit, ct), ct);
    public Task<StockMovement> ReverseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, Guid operationId, string reference, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.reverse", (service, session) => service.ReverseAsync(session.Actor, id, version, operationId, reference, ct), ct);
    public Task<StockReservation> ReserveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid variantId, Guid warehouseId, decimal quantity, string reference, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.reserve", (service, session) => service.ReserveAsync(session.Actor, operationId, variantId, warehouseId, quantity, reference, ct), ct);
    public Task<StockReservation> GetReservationAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.read", (service, session) => service.GetReservationAsync(session.Actor, id, ct), ct);
    public Task<StockReservation> ReleaseReservationAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.release", (service, session) => service.ReleaseReservationAsync(session.Actor, id, version, ct), ct);
    public Task<StockMovement> ConsumeReservationAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "stock.consume", (service, session) => service.ConsumeReservationAsync(session.Actor, id, version, ct), ct);

    private Task<T> Run<T>(string userId, Guid tenantId, Guid organizationId, string permission,
        Func<StockService, Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty) throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout, new("NexaOne.MES", Text(tenantId), Text(organizationId)),
                _memberships, _masters, _clock, _batchLimitSql);
            try
            {
                await session.Authorize(userId, permission, ct);
                var result = await action(StockService.Create(session, session, _clock), session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private static bool ValidText(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > max || value != value.Trim()) return false;
        for (var i = 0; i < value.Length; i++)
            if (char.IsSurrogate(value[i]) && (!char.IsHighSurrogate(value[i]) || ++i >= value.Length || !char.IsLowSurrogate(value[i]))) return false;
        return true;
    }
    private static void RequireText(string? value, int max) { if (!ValidText(value, max)) throw Failure("INVALID_BUSINESS_INPUT"); }
    private static BusinessException Failure(string code) => new(code);
    private static string Text(Guid value) => value.ToString("D");
    private static string? Text(Guid? value) => value.HasValue ? Text(value.Value) : null;
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && Text(id) == value
        ? id : throw new InvalidDataException("Stock storage contains an invalid identity or version.");
    private static Guid? OptionalId(string? value) => value is null ? null : Id(value);
    // TEXT avoids SQLite NUMERIC affinity converting large or fractional values to binary floating point.
    // Canonical plain decimals also make the nonzero reference check provider independent.
    private static string Quantity(decimal value)
    {
        if (decimal.Round(value, 6) != value) throw Failure("STORAGE_CONTRACT_VIOLATION");
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }
    private static decimal Quantity(string value)
        => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result) && Quantity(result) == value
            ? result : throw new InvalidDataException("Stock storage contains a noncanonical quantity.");

    // Confined to one processor-owned transaction. This adapter never commits, retries or caches authority.
    private sealed class Session(DbConnection connection, DbTransaction transaction, int? timeout, BusinessScope scope,
        IBusinessMembershipBridge memberships, IBusinessMasterDirectory masters, TimeProvider clock, string batchLimitSql)
        : IAtomicBusinessStore<IStockTransaction>, IStockTransaction, IBusinessAuthorizer
    {
        private bool _open = true;
        private int _invoked;
        private string[] _grants = [];
        private const int ListBatchSize = 128;
        private const string ProductColumns = "PRODUCT_ID AS Id, VARIANT_ID AS VariantId, VERSION AS Version, MASTER_PRODUCT_ID AS MasterId, UNIT AS Unit";
        private const string WarehouseColumns = "WAREHOUSE_ID AS Id, VERSION AS Version, CODE AS Code, NAME AS Name, IS_ACTIVE AS Active";
        internal BusinessActor Actor { get; private set; } = null!;
        public BusinessScope Scope { get; } = scope;
        internal void Close() => _open = false;
        private CommandDefinition Command(string sql, object? values, CancellationToken ct)
        {
            if (!_open) throw new ObjectDisposedException(nameof(Session));
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters(values);
            parameters.Add("TenantId", Scope.TenantId); parameters.Add("OrganizationId", Scope.OrganizationId);
            return new(sql, parameters, transaction, commandTimeout: timeout, cancellationToken: ct);
        }
        private Task<T?> Row<T>(string sql, object? values, CancellationToken ct) where T : class
            => connection.QuerySingleOrDefaultAsync<T>(Command(sql, values, ct));
        private async Task<T> Scalar<T>(string sql, object? values, CancellationToken ct)
            => (await connection.ExecuteScalarAsync<T>(Command(sql, values, ct)))!;
        private async Task Write(string sql, object values, CancellationToken ct)
        {
            if (await connection.ExecuteAsync(Command(sql, values, ct)) != 1)
                throw new DBConcurrencyException("Stock write did not affect exactly one row.");
        }
        internal async Task Authorize(string userId, string permission, CancellationToken ct)
        {
            BusinessMembership? membership;
            try { membership = await memberships.GetAccessInTransactionAsync(transaction, userId, Id(Scope.TenantId), Id(Scope.OrganizationId), ct); }
            catch (InvalidDataException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
            var plant = await Scalar<string?>("SELECT PLANT_ID FROM IVT_BUSINESS_SCOPE WHERE " + ScopeWhere + " AND IS_ACTIVE=1", null, ct);
            if (membership is null || !membership.Permissions.Contains(permission, StringComparer.Ordinal)
                || plant is null || await masters.FindPlantAsync(transaction, plant, ct) is null)
                throw Failure("BUSINESS_ACCESS_DENIED");
            Actor = new(Text(membership.BusinessUserId), Scope); _grants = membership.Permissions.ToArray();
        }
        public Task<bool> IsAllowedAsync(BusinessActor actor, string permission, string resourceType, Guid? resourceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_open && actor == Actor && _grants.Contains(permission, StringComparer.Ordinal));
        }
        public async Task<T> ExecuteAsync<T>(BusinessScope requestedScope, Func<IStockTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            if (!_open || requestedScope != Scope) throw Failure("STORAGE_SCOPE_VIOLATION");
            if (Interlocked.Exchange(ref _invoked, 1) != 0) throw Failure("TRANSACTION_REPLAY_NOT_ALLOWED");
            ct.ThrowIfCancellationRequested(); var result = await work(this, ct); ct.ThrowIfCancellationRequested(); return result;
        }
        internal Task<ProductDto?> Master(string id, CancellationToken ct) => masters.FindProductAsync(transaction, id, ct);
        internal Task<EnrolledProduct?> ProductRow(string predicate, string key, CancellationToken ct)
            => Row<EnrolledProduct>("SELECT " + ProductColumns + " FROM IVT_STOCK_PRODUCT WHERE "
                + ScopeWhere + " AND " + predicate, new { key }, ct);
        internal async Task<ProductVariant> Enroll(string productId, string unit, CancellationToken ct)
        {
            RequireText(productId, 50); RequireText(unit, 40);
            var master = await Master(productId, ct) ?? throw Failure("PRODUCT_NOT_FOUND");
            if (master.ValidState != "Valid") throw Failure("PRODUCT_INACTIVE");
            if (master.Unit != unit) throw Failure("PRODUCT_UNIT_CHANGED");
            var current = await ProductRow("MASTER_PRODUCT_ID=@key", master.ProductId, ct);
            if (current is not null)
            {
                if (current.Unit != unit) throw Failure("PRODUCT_UNIT_CHANGED");
                return await Variant(current, ct);
            }
            var id = Guid.NewGuid(); var variantId = Guid.NewGuid(); var version = Guid.NewGuid();
            await Write("""
                INSERT INTO IVT_STOCK_PRODUCT (TENANT_ID, ORGANIZATION_ID, PRODUCT_ID, VARIANT_ID, VERSION, MASTER_PRODUCT_ID, UNIT)
                VALUES (@TenantId, @OrganizationId, @Id, @Variant, @Version, @Master, @Unit)
                """, new { Id = Text(id), Variant = Text(variantId), Version = Text(version), Master = master.ProductId, Unit = unit }, ct);
            await AppendAuditAsync(new(Guid.NewGuid(), Actor, "stock-product", id, "enrolled", null, version, clock.GetUtcNow()), ct);
            return new(variantId, Scope, version, id, master.ProductId, unit, Array.Empty<OptionSelection>());
        }
        public async Task<Product?> FindProductAsync(Guid id, CancellationToken ct)
        {
            var row = await ProductRow("PRODUCT_ID=@key", Text(id), ct);
            if (row is null) return null;
            var master = await Master(row.MasterId, ct) ?? throw Failure("PRODUCT_NOT_FOUND");
            return Product(row, master);
        }
        private Product Product(EnrolledProduct row, ProductDto master)
            => new(Id(row.Id), Scope, Id(row.Version), new(master.ProductId, master.ProductName, master.Description), master.ValidState == "Valid");

        public async Task<BusinessPage<Product>> QueryProductsAsync(InventoryQuery query, CancellationToken ct)
        {
            // O(enrolled products): live MDM name/active filters must precede both offset and total.
            // Keyset batches bound materialization and owner calls; only the requested page is retained.
            var items = new List<Product>(query.Limit);
            long total = 0;
            var end = (long)query.Offset + query.Limit;
            string? afterMaster = null, afterId = null;
            while (true)
            {
                var rows = (await connection.QueryAsync<EnrolledProduct>(Command("SELECT " + ProductColumns
                    + " FROM IVT_STOCK_PRODUCT WHERE " + ScopeWhere
                    + " AND (@AfterMaster IS NULL OR MASTER_PRODUCT_ID>@AfterMaster OR (MASTER_PRODUCT_ID=@AfterMaster AND PRODUCT_ID>@AfterId))"
                    + " ORDER BY MASTER_PRODUCT_ID, PRODUCT_ID" + batchLimitSql,
                    new { AfterMaster = afterMaster, AfterId = afterId, BatchSize = ListBatchSize }, ct))).ToArray();
                if (rows.Length == 0) break;
                var keys = rows.Select(row => row.MasterId).ToArray();
                if (keys.Any(key => !ValidText(key, 50)) || keys.Distinct(StringComparer.Ordinal).Count() != keys.Length)
                    throw Failure("STORAGE_CONTRACT_VIOLATION");
                var found = await masters.FindProductsAsync(transaction, keys, ct);
                if (found is null || found.Count != rows.Length) throw Failure("STORAGE_CONTRACT_VIOLATION");
                var byId = new Dictionary<string, ProductDto>(StringComparer.Ordinal);
                var expected = new HashSet<string>(keys, StringComparer.Ordinal);
                foreach (var master in found)
                    if (master is null || master.ProductId is null || !expected.Contains(master.ProductId)
                        || master.ProductName is null || !byId.TryAdd(master.ProductId, master))
                        throw Failure("STORAGE_CONTRACT_VIOLATION");
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!byId.TryGetValue(row.MasterId, out var master)) throw Failure("STORAGE_CONTRACT_VIOLATION");
                    var product = Product(row, master);
                    if ((!query.IncludeInactive && !product.Active) || !Matches(query.Text, product.Input.Code, product.Input.Name)) continue;
                    if (total >= query.Offset && total < end) items.Add(product);
                    total++;
                }
                afterMaster = rows[^1].MasterId; afterId = rows[^1].Id;
                if (rows.Length < ListBatchSize) break;
            }
            return new(Array.AsReadOnly(items.ToArray()), total);
        }

        private static bool Matches(string? text, string code, string name)
            => string.IsNullOrEmpty(text) || code.Contains(text, StringComparison.OrdinalIgnoreCase)
                || name.Contains(text, StringComparison.OrdinalIgnoreCase);
        internal async Task<ProductVariant> Variant(EnrolledProduct row, CancellationToken ct)
        {
            var master = await Master(row.MasterId, ct) ?? throw Failure("PRODUCT_NOT_FOUND");
            return new(Id(row.VariantId), Scope, Id(row.Version), Id(row.Id), row.MasterId, row.Unit,
                Array.Empty<OptionSelection>(), master.ValidState == "Valid" && master.Unit == row.Unit);
        }
        public async Task<ProductVariant?> FindVariantAsync(Guid id, CancellationToken ct)
        {
            var row = await ProductRow("VARIANT_ID=@key", Text(id), ct);
            return row is null ? null : await Variant(row, ct);
        }
        internal Task<CreationRow?> Creation(Guid operationId, CancellationToken ct)
            => Row<CreationRow>("SELECT WAREHOUSE_ID AS Id, REQUESTED_BY AS Actor, CODE AS Code, NAME AS Name FROM IVT_STOCK_WAREHOUSE_CREATION WHERE "
                + ScopeWhere + " AND OPERATION_ID=@id", new { id = Text(operationId) }, ct);
        internal Task RecordCreation(Guid operationId, Warehouse value, CancellationToken ct)
            => Write("""
                INSERT INTO IVT_STOCK_WAREHOUSE_CREATION (TENANT_ID, ORGANIZATION_ID, OPERATION_ID, WAREHOUSE_ID, REQUESTED_BY, CODE, NAME)
                VALUES (@TenantId, @OrganizationId, @Operation, @Id, @Actor, @Code, @Name)
                """, new { Operation = Text(operationId), Id = Text(value.Id), Actor = Actor.UserId, value.Code, value.Name }, ct);
        public async Task<Warehouse?> FindWarehouseAsync(Guid id, CancellationToken ct)
        {
            var row = await Row<WarehouseRow>("SELECT " + WarehouseColumns + " FROM IVT_STOCK_WAREHOUSE WHERE "
                + ScopeWhere + " AND WAREHOUSE_ID=@id", new { id = Text(id) }, ct);
            return row is null ? null : Warehouse(row);
        }
        private Warehouse Warehouse(WarehouseRow row)
            => new(Id(row.Id), Scope, Id(row.Version), row.Code, row.Name, row.Active);

        public async Task<BusinessPage<Warehouse>> QueryWarehousesAsync(InventoryQuery query, CancellationToken ct)
        {
            // O(scope rows): scan active candidates in bounded batches for identical ordinal text semantics
            // on SQLite and SQL Server. Count after filtering, retaining only the requested page.
            var items = new List<Warehouse>(query.Limit);
            long total = 0;
            var end = (long)query.Offset + query.Limit;
            string? afterCode = null, afterId = null;
            while (true)
            {
                var rows = (await connection.QueryAsync<WarehouseRow>(Command("SELECT " + WarehouseColumns
                    + " FROM IVT_STOCK_WAREHOUSE WHERE " + ScopeWhere
                    + " AND (@IncludeInactive=1 OR IS_ACTIVE=1)"
                    + " AND (@AfterCode IS NULL OR CODE>@AfterCode OR (CODE=@AfterCode AND WAREHOUSE_ID>@AfterId))"
                    + " ORDER BY CODE, WAREHOUSE_ID" + batchLimitSql,
                    new { query.IncludeInactive, AfterCode = afterCode, AfterId = afterId, BatchSize = ListBatchSize }, ct))).ToArray();
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!Matches(query.Text, row.Code, row.Name)) continue;
                    if (total >= query.Offset && total < end) items.Add(Warehouse(row));
                    total++;
                }
                if (rows.Length < ListBatchSize) break;
                afterCode = rows[^1].Code; afterId = rows[^1].Id;
            }
            return new(Array.AsReadOnly(items.ToArray()), total);
        }
        public async Task<bool> HasWarehouseStockOrReservationsAsync(Guid id, CancellationToken ct)
            => await Scalar<long>("SELECT COUNT(*) FROM IVT_STOCK_BALANCE WHERE " + ScopeWhere + " AND WAREHOUSE_ID=@id AND (ON_HAND<>'0' OR RESERVED<>'0')", new { id = Text(id) }, ct) != 0
                || await Scalar<long>("SELECT COUNT(*) FROM IVT_STOCK_RESERVATION WHERE " + ScopeWhere + " AND WAREHOUSE_ID=@id AND STATE=0", new { id = Text(id) }, ct) != 0;
        public async Task SaveWarehouseAsync(Warehouse value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope); RequireText(value.Code, 80);
            if (await Scalar<long>("SELECT COUNT(*) FROM IVT_STOCK_WAREHOUSE WHERE " + ScopeWhere + " AND CODE=@Code AND WAREHOUSE_ID<>@Id",
                new { value.Code, Id = Text(value.Id) }, ct) != 0) throw Failure("WAREHOUSE_CODE_CONFLICT");
            await Write(expectedVersion is null ? """
                INSERT INTO IVT_STOCK_WAREHOUSE (TENANT_ID, ORGANIZATION_ID, WAREHOUSE_ID, VERSION, CODE, NAME, IS_ACTIVE)
                VALUES (@TenantId, @OrganizationId, @Id, @Version, @Code, @Name, @Active)
                """ : "UPDATE IVT_STOCK_WAREHOUSE SET VERSION=@Version, CODE=@Code, NAME=@Name, IS_ACTIVE=@Active WHERE "
                + ScopeWhere + " AND WAREHOUSE_ID=@Id AND VERSION=@Previous",
                new { Id = Text(value.Id), Version = Text(value.Version), value.Code, value.Name, value.Active, Previous = Text(expectedVersion) }, ct);
        }
        public async Task<StockBalance?> FindBalanceAsync(Guid variantId, Guid warehouseId, CancellationToken ct)
        {
            var row = await Row<BalanceRow>("SELECT BALANCE_ID AS Id, VERSION AS Version, ON_HAND AS OnHand, RESERVED AS Reserved FROM IVT_STOCK_BALANCE WHERE "
                + ScopeWhere + " AND VARIANT_ID=@Variant AND WAREHOUSE_ID=@Warehouse", new { Variant = Text(variantId), Warehouse = Text(warehouseId) }, ct);
            return row is null ? null : new(Id(row.Id), Scope, Id(row.Version), variantId, warehouseId, Quantity(row.OnHand), Quantity(row.Reserved));
        }
        public Task SaveBalanceAsync(StockBalance value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            if (value.OnHand < 0m || value.Reserved < 0m || value.Reserved > value.OnHand) throw Failure("STORAGE_CONTRACT_VIOLATION");
            return Write(expectedVersion is null ? """
                INSERT INTO IVT_STOCK_BALANCE (TENANT_ID, ORGANIZATION_ID, BALANCE_ID, VERSION, VARIANT_ID, WAREHOUSE_ID, ON_HAND, RESERVED)
                VALUES (@TenantId, @OrganizationId, @Id, @Version, @Variant, @Warehouse, @OnHand, @Reserved)
                """ : "UPDATE IVT_STOCK_BALANCE SET VERSION=@Version, ON_HAND=@OnHand, RESERVED=@Reserved WHERE "
                + ScopeWhere + " AND BALANCE_ID=@Id AND VERSION=@Previous AND VARIANT_ID=@Variant AND WAREHOUSE_ID=@Warehouse",
                new { Id = Text(value.Id), Version = Text(value.Version), Variant = Text(value.VariantId), Warehouse = Text(value.WarehouseId),
                    OnHand = Quantity(value.OnHand), Reserved = Quantity(value.Reserved), Previous = Text(expectedVersion) }, ct);
        }
        private const string MovementColumns = "MOVEMENT_ID AS Id, VERSION AS Version, OPERATION_ID AS OperationId, VARIANT_ID AS VariantId, "
            + "KIND AS Kind, QUANTITY AS Quantity, FROM_WAREHOUSE_ID AS FromId, TO_WAREHOUSE_ID AS ToId, REFERENCE AS Reference, "
            + "CREATED_BY AS CreatedBy, DELTAS_JSON AS Deltas, REVERSAL_OF_ID AS ReversalOf, REVERSED_BY_ID AS ReversedBy";
        private StockMovement Movement(MovementRow row)
            => new(Id(row.Id), Scope, Id(row.Version), new(Id(row.OperationId), Id(row.VariantId), (StockMovementKind)row.Kind,
                Quantity(row.Quantity), OptionalId(row.FromId), OptionalId(row.ToId), row.Reference), Text(Id(row.CreatedBy)),
                Array.AsReadOnly(JsonSerializer.Deserialize<StockDelta[]>(row.Deltas) ?? throw new InvalidDataException("Missing stock deltas.")),
                OptionalId(row.ReversalOf), OptionalId(row.ReversedBy));
        private async Task<StockMovement?> ReadMovement(string predicate, Guid id, CancellationToken ct)
        {
            var row = await Row<MovementRow>("SELECT " + MovementColumns + " FROM IVT_STOCK_MOVEMENT WHERE " + ScopeWhere + " AND " + predicate,
                new { id = Text(id) }, ct);
            return row is null ? null : Movement(row);
        }
        public Task<StockMovement?> FindMovementAsync(Guid id, CancellationToken ct) => ReadMovement("MOVEMENT_ID=@id", id, ct);
        public Task<StockMovement?> FindMovementByOperationAsync(Guid id, CancellationToken ct) => ReadMovement("OPERATION_ID=@id", id, ct);
        public async Task<BusinessPage<StockMovement>> QueryMovementsAsync(Guid variantId, int offset, int limit, CancellationToken ct)
        {
            var values = new { Variant = Text(variantId), Offset = offset, End = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM IVT_STOCK_MOVEMENT WHERE " + ScopeWhere + " AND VARIANT_ID=@Variant", values, ct);
            var rows = await connection.QueryAsync<MovementRow>(Command("SELECT * FROM (SELECT " + MovementColumns
                + ", ROW_NUMBER() OVER (ORDER BY AT_TICKS, MOVEMENT_ID) AS RowNumber FROM IVT_STOCK_MOVEMENT WHERE "
                + ScopeWhere + " AND VARIANT_ID=@Variant) AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber", values, ct));
            return new(Array.AsReadOnly(rows.Select(Movement).ToArray()), total);
        }
        public async Task SaveMovementAsync(StockMovement value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            if (expectedVersion.HasValue)
            {
                var current = await FindMovementAsync(value.Id, ct) ?? throw Failure("STOCK_MOVEMENT_NOT_FOUND");
                if (current.Version != expectedVersion || current.Posting != value.Posting || current.CreatedBy != value.CreatedBy
                    || !current.Deltas.SequenceEqual(value.Deltas) || current.ReversalOfId != value.ReversalOfId
                    || current.ReversedById is not null || value.ReversedById is null || current.ReversalOfId is not null)
                    throw Failure("STORAGE_CONTRACT_VIOLATION");
                await Write("UPDATE IVT_STOCK_MOVEMENT SET VERSION=@Version, REVERSED_BY_ID=@ReversedBy WHERE " + ScopeWhere
                    + " AND MOVEMENT_ID=@Id AND VERSION=@Previous AND REVERSED_BY_ID IS NULL AND REVERSAL_OF_ID IS NULL",
                    new { Id = Text(value.Id), Version = Text(value.Version), Previous = Text(expectedVersion), ReversedBy = Text(value.ReversedById) }, ct);
                return;
            }
            await Write("""
                INSERT INTO IVT_STOCK_MOVEMENT (TENANT_ID, ORGANIZATION_ID, MOVEMENT_ID, VERSION, OPERATION_ID, VARIANT_ID,
                    KIND, QUANTITY, FROM_WAREHOUSE_ID, TO_WAREHOUSE_ID, REFERENCE, CREATED_BY, DELTAS_JSON, REVERSAL_OF_ID, REVERSED_BY_ID, AT_TICKS)
                VALUES (@TenantId, @OrganizationId, @Id, @Version, @Operation, @Variant, @Kind, @Quantity, @FromId, @ToId,
                    @Reference, @CreatedBy, @Deltas, @ReversalOf, @ReversedBy, @At)
                """, new { Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.Posting.OperationId), Variant = Text(value.Posting.VariantId),
                    Kind = (int)value.Posting.Kind, Quantity = Quantity(value.Posting.Quantity), FromId = Text(value.Posting.FromWarehouseId), ToId = Text(value.Posting.ToWarehouseId),
                    value.Posting.Reference, value.CreatedBy, Deltas = JsonSerializer.Serialize(value.Deltas), ReversalOf = Text(value.ReversalOfId),
                    ReversedBy = Text(value.ReversedById), At = clock.GetUtcNow().UtcTicks }, ct);
        }
        private async Task<StockReservation?> ReadReservation(string predicate, Guid id, CancellationToken ct)
        {
            var row = await Row<ReservationRow>("SELECT RESERVATION_ID AS Id, VERSION AS Version, OPERATION_ID AS OperationId, VARIANT_ID AS VariantId, "
                + "WAREHOUSE_ID AS WarehouseId, QUANTITY AS Quantity, REFERENCE AS Reference, CREATED_BY AS CreatedBy, STATE AS State "
                + "FROM IVT_STOCK_RESERVATION WHERE " + ScopeWhere + " AND " + predicate, new { id = Text(id) }, ct);
            return row is null ? null : new(Id(row.Id), Scope, Id(row.Version), Id(row.OperationId), Id(row.VariantId), Id(row.WarehouseId),
                Quantity(row.Quantity), row.Reference, Text(Id(row.CreatedBy)), (StockReservationState)row.State);
        }
        public Task<StockReservation?> FindReservationAsync(Guid id, CancellationToken ct) => ReadReservation("RESERVATION_ID=@id", id, ct);
        public Task<StockReservation?> FindReservationByOperationAsync(Guid id, CancellationToken ct) => ReadReservation("OPERATION_ID=@id", id, ct);
        public async Task<BusinessPage<StockReservation>> QueryReservationsAsync(Guid? variantId, Guid? warehouseId, StockReservationState? state,
            int offset, int limit, CancellationToken ct)
        {
            // Optional filters are parameterized and null-skipped. The table has no creation clock, so the stable
            // order is the reservation ID itself; callers must not read it as chronological order.
            const string filter = " AND (@Variant IS NULL OR VARIANT_ID=@Variant) AND (@Warehouse IS NULL OR WAREHOUSE_ID=@Warehouse) AND (@State IS NULL OR STATE=@State)";
            var values = new
            {
                Variant = variantId.HasValue ? Text(variantId.Value) : null, Warehouse = warehouseId.HasValue ? Text(warehouseId.Value) : null,
                State = state.HasValue ? (int?)state.Value : null, Offset = offset, End = (long)offset + limit
            };
            var total = await Scalar<long>("SELECT COUNT(*) FROM IVT_STOCK_RESERVATION WHERE " + ScopeWhere + filter, values, ct);
            var rows = await connection.QueryAsync<ReservationRow>(Command("SELECT * FROM (SELECT RESERVATION_ID AS Id, VERSION AS Version, OPERATION_ID AS OperationId, "
                + "VARIANT_ID AS VariantId, WAREHOUSE_ID AS WarehouseId, QUANTITY AS Quantity, REFERENCE AS Reference, CREATED_BY AS CreatedBy, STATE AS State, "
                + "ROW_NUMBER() OVER (ORDER BY RESERVATION_ID) AS RowNumber FROM IVT_STOCK_RESERVATION WHERE " + ScopeWhere + filter
                + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber", values, ct));
            return new(Array.AsReadOnly(rows.Select(row => new StockReservation(Id(row.Id), Scope, Id(row.Version), Id(row.OperationId), Id(row.VariantId),
                Id(row.WarehouseId), Quantity(row.Quantity), row.Reference, Text(Id(row.CreatedBy)), (StockReservationState)row.State)).ToArray()), total);
        }
        public Task SaveReservationAsync(StockReservation value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            return Write(expectedVersion is null ? """
                INSERT INTO IVT_STOCK_RESERVATION (TENANT_ID, ORGANIZATION_ID, RESERVATION_ID, VERSION, OPERATION_ID, VARIANT_ID,
                    WAREHOUSE_ID, QUANTITY, REFERENCE, CREATED_BY, STATE)
                VALUES (@TenantId, @OrganizationId, @Id, @Version, @Operation, @Variant, @Warehouse, @Quantity, @Reference, @CreatedBy, @State)
                """ : "UPDATE IVT_STOCK_RESERVATION SET VERSION=@Version, STATE=@State WHERE " + ScopeWhere
                + " AND RESERVATION_ID=@Id AND VERSION=@Previous AND OPERATION_ID=@Operation AND VARIANT_ID=@Variant"
                + " AND WAREHOUSE_ID=@Warehouse AND QUANTITY=@Quantity AND REFERENCE=@Reference AND CREATED_BY=@CreatedBy AND STATE=0",
                new { Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId), Variant = Text(value.VariantId), Warehouse = Text(value.WarehouseId),
                    Quantity = Quantity(value.Quantity), value.Reference, value.CreatedBy, State = (int)value.State, Previous = Text(expectedVersion) }, ct);
        }
        public Task AppendAuditAsync(BusinessAudit audit, CancellationToken ct)
        {
            if (audit.Actor != Actor) throw Failure("STORAGE_SCOPE_VIOLATION");
            return Write("""
                INSERT INTO IVT_STOCK_AUDIT (TENANT_ID, ORGANIZATION_ID, AUDIT_ID, USER_ID, RESOURCE_TYPE,
                    RESOURCE_ID, OPERATION, PREVIOUS_VERSION, VERSION, AT_TICKS)
                VALUES (@TenantId, @OrganizationId, @Id, @UserId, @ResourceType, @ResourceId, @Operation, @Previous, @Version, @At)
                """, new { Id = Text(audit.Id), audit.Actor.UserId, audit.ResourceType, ResourceId = Text(audit.ResourceId), audit.Operation,
                    Previous = Text(audit.PreviousVersion), Version = Text(audit.Version), At = audit.At.UtcTicks }, ct);
        }
        private void RequireScope(BusinessScope value) { if (value != Scope) throw Failure("STORAGE_SCOPE_VIOLATION"); }
    }

    private sealed class EnrolledProduct
    {
        public string Id { get; set; } = "";
        public string VariantId { get; set; } = "";
        public string Version { get; set; } = "";
        public string MasterId { get; set; } = "";
        public string Unit { get; set; } = "";
    }
    private sealed class WarehouseRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Active { get; set; }
    }
    private sealed class CreationRow
    {
        public string Id { get; set; } = "";
        public string Actor { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
    }
    private sealed class BalanceRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string OnHand { get; set; } = "";
        public string Reserved { get; set; } = "";
    }
    private sealed class MovementRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string VariantId { get; set; } = "";
        public int Kind { get; set; }
        public string Quantity { get; set; } = "";
        public string? FromId { get; set; }
        public string? ToId { get; set; }
        public string Reference { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public string Deltas { get; set; } = "";
        public string? ReversalOf { get; set; }
        public string? ReversedBy { get; set; }
    }
    private sealed class ReservationRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string VariantId { get; set; } = "";
        public string WarehouseId { get; set; } = "";
        public string Quantity { get; set; } = "";
        public string Reference { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public int State { get; set; }
    }
}
