using System.Data;
using System.Data.Common;
using Dapper;
using NexaDB.Data.Abstractions.Models;
using NexaFramework.Service;
using NexaFramework.Service.Inventory;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Ivt;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.IVT.Infrastructure;

/// <summary>Owns shared-asset persistence. SYS identity and MDM plant/worker references are read
/// in the same serializable transaction as the IVT writes; no authority is taken from plant claims.</summary>
public sealed class EquipmentSharingBridge
    : IEquipmentSharingBridge
{
    private const string ScopeWhere = "TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId";
    private readonly ServiceObjectProcessor _processor;
    private readonly int? _timeout;
    private readonly TimeProvider _clock;
    private readonly IBusinessMembershipBridge _memberships;
    private readonly IBusinessMasterDirectory _masters;
    private readonly string _batchLimitSql;

    public EquipmentSharingBridge(EesDataSource dataSource, IBusinessMembershipBridge memberships,
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

    public Task<InventoryScopeBinding> GetScopeBindingAsync(string administratorId, Guid tenantId,
        Guid organizationId, CancellationToken ct = default)
        => InTransaction(administratorId, tenantId, organizationId, async session =>
        {
            await session.RequireAdministrator(administratorId, ct);
            return await session.ReadBinding(ct) ?? throw Failure("SCOPE_NOT_FOUND");
        }, ct);

    public Task<InventoryScopeBinding> BindScopeAsync(string administratorId, Guid tenantId, Guid organizationId,
        string plantId, Guid? expectedVersion, bool active, CancellationToken ct = default)
        => InTransaction(administratorId, tenantId, organizationId, async session =>
        {
            var administrator = await session.RequireAdministrator(administratorId, ct);
            if (!ValidKey(plantId) || expectedVersion == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var canonicalPlant = await session.FindPlant(plantId, ct)
                ?? throw Failure("PLANT_NOT_FOUND");
            var current = await session.ReadBinding(ct);
            if (current?.Version != expectedVersion) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (current is not null && current.PlantId != canonicalPlant) throw Failure("SCOPE_PLANT_IMMUTABLE");
            if (!active && await session.Scalar<long>("SELECT COUNT(*) FROM IVT_SHARED_BOOKING WHERE " + ScopeWhere
                + " AND STATE IN (0,1,2)", null, ct) != 0) throw Failure("SCOPE_HAS_OPEN_BOOKINGS");
            if (!active && (await session.Scalar<long>("SELECT COUNT(*) FROM IVT_STOCK_BALANCE WHERE " + ScopeWhere
                + " AND (ON_HAND<>'0' OR RESERVED<>'0')", null, ct) != 0
                || await session.Scalar<long>("SELECT COUNT(*) FROM IVT_STOCK_RESERVATION WHERE " + ScopeWhere
                + " AND STATE=0", null, ct) != 0)) throw Failure("SCOPE_HAS_STOCK_OR_RESERVATIONS");
            var next = new InventoryScopeBinding(tenantId, organizationId, canonicalPlant, Guid.NewGuid(), active);
            var values = new { PlantId = canonicalPlant, Version = Text(next.Version), Active = active,
                Previous = expectedVersion.HasValue ? Text(expectedVersion.Value) : null,
                Administrator = administrator, Now = _clock.GetUtcNow().UtcDateTime };
            await session.Write(current is null ? """
                INSERT INTO IVT_BUSINESS_SCOPE (TENANT_ID, ORGANIZATION_ID, PLANT_ID, VERSION, IS_ACTIVE, UPDATED_BY, UPDATED_AT)
                VALUES (@TenantId, @OrganizationId, @PlantId, @Version, @Active, @Administrator, @Now)
                """ : "UPDATE IVT_BUSINESS_SCOPE SET IS_ACTIVE=@Active, VERSION=@Version, UPDATED_BY=@Administrator, UPDATED_AT=@Now WHERE "
                + ScopeWhere + " AND VERSION=@Previous", values, ct);
            await session.Write("""
                INSERT INTO IVT_BUSINESS_SCOPE_AUDIT (TENANT_ID, ORGANIZATION_ID, VERSION, PLANT_ID, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
                VALUES (@TenantId, @OrganizationId, @Version, @PlantId, @Active, @Administrator, @Now)
                """, values, ct);
            return next;
        }, ct);

    public Task<SharedEquipment> CreateEquipmentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, string code, string name, int capacity, bool requiresApproval, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.write", async (service, session) =>
        {
            if (operationId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var prior = await session.FindCreation(operationId, ct);
            if (prior is not null)
            {
                if (prior.RequestedBy != session.Actor.UserId || prior.Code != code || prior.Name != name
                    || prior.Capacity != capacity || prior.RequiresApproval != requiresApproval)
                    throw Failure("EQUIPMENT_CREATE_OPERATION_CONFLICT");
                return await session.FindEquipmentAsync(Id(prior.EquipmentId), ct) ?? throw Failure("EQUIPMENT_NOT_FOUND");
            }
            var asset = await service.SaveEquipmentAsync(session.Actor, code, name, capacity, requiresApproval, ct: ct);
            await session.RecordCreation(operationId, asset, ct);
            return asset;
        }, ct);

    public Task<SharedEquipment> UpdateEquipmentAsync(string userId, Guid tenantId, Guid organizationId,
        string code, string name, int capacity, bool requiresApproval, Guid id,
        Guid expectedVersion, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.write",
            (service, session) => service.SaveEquipmentAsync(session.Actor, code, name, capacity, requiresApproval, id, expectedVersion, ct), ct);
    public Task<SharedEquipment> GetEquipmentAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.read", (service, session) => service.GetEquipmentAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<SharedEquipment>> ListEquipmentAsync(string userId, Guid tenantId, Guid organizationId,
        InventoryQuery query, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.read", (service, session) => service.ListEquipmentAsync(session.Actor, query, ct), ct);
    public Task<SharedEquipment> GetEquipmentByCodeAsync(string userId, Guid tenantId, Guid organizationId, string code, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.read", async (service, session) =>
        {
            RequireCode(code);
            var id = await session.Scalar<string?>("SELECT EQUIPMENT_ID FROM IVT_SHARED_EQUIPMENT WHERE " + ScopeWhere
                + " AND CODE=@code", new { code }, ct) ?? throw Failure("EQUIPMENT_NOT_FOUND");
            return await service.GetEquipmentAsync(session.Actor, Id(id), ct);
        }, ct);
    public Task<SharedEquipment> SetEquipmentActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.write", (service, session) => service.SetEquipmentActiveAsync(session.Actor, id, version, active, ct), ct);
    public Task<EquipmentBooking> RequestBookingAsync(string userId, Guid tenantId, Guid organizationId,
        Guid bookingId, Guid equipmentId, string workerId, DateTimeOffset start, DateTimeOffset end, int quantity, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.booking.request", async (service, session) =>
            await service.RequestBookingAsync(session.Actor, bookingId, equipmentId,
                await session.ResolveWorker(workerId, ct), start, end, quantity, ct), ct);
    public Task<EquipmentBooking> GetBookingAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.booking.read", (service, session) => service.GetBookingAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<EquipmentBooking>> ListBookingsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? equipmentId = null, EquipmentBookingState? state = null, int offset = 0, int limit = 50, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.booking.read",
            (service, session) => service.ListBookingsAsync(session.Actor, equipmentId, state, offset, limit, ct), ct);
    public Task<EquipmentBooking> DecideBookingAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool approve, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.booking.decide", (service, session) => service.DecideBookingAsync(session.Actor, id, version, approve, ct), ct);
    public Task<EquipmentBooking> CancelBookingAsync(string userId, Guid tenantId, Guid organizationId, Guid id, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.booking.cancel", (service, session) => service.CancelBookingAsync(session.Actor, id, version, ct), ct);
    public Task<EquipmentBooking> CheckOutAsync(string userId, Guid tenantId, Guid organizationId, Guid id, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.booking.checkout", (service, session) => service.CheckOutAsync(session.Actor, id, version, ct), ct);
    public Task<EquipmentBooking> ReturnAsync(string userId, Guid tenantId, Guid organizationId, Guid id, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "equipment.booking.return", (service, session) => service.ReturnAsync(session.Actor, id, version, ct), ct);

    private Task<T> Run<T>(string userId, Guid tenantId, Guid organizationId, string permission,
        Func<EquipmentSharingService, Session, Task<T>> action, CancellationToken ct)
        => InTransaction(userId, tenantId, organizationId, async session =>
        {
            await session.Authorize(userId, permission, ct);
            return await action(EquipmentSharingService.Create(session, session, _clock), session);
        }, ct);

    private Task<T> InTransaction<T>(string userId, Guid tenantId, Guid organizationId, Func<Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidKey(userId) || tenantId == Guid.Empty || organizationId == Guid.Empty) throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout, new("NexaOne.MES", Text(tenantId), Text(organizationId)),
                _memberships, _masters, _batchLimitSql);
            try { return await action(session); }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private static bool ValidKey(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 50 && value == value.Trim();
    private static void RequireCode(string code)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 80 || code != code.Trim()) throw Failure("INVALID_EQUIPMENT_CODE");
    }
    private static BusinessException Failure(string code) => new(code);
    private static string Text(Guid value) => value.ToString("D");
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && Text(id) == value
        ? id : throw new InvalidDataException("Shared-asset storage contains an invalid identity or version.");

    // This object is confined to one processor-owned database transaction. It neither commits nor retries.
    private sealed class Session(DbConnection connection, DbTransaction transaction, int? timeout, BusinessScope scope,
        IBusinessMembershipBridge memberships, IBusinessMasterDirectory masters, string batchLimitSql)
        : IAtomicBusinessStore<IEquipmentTransaction>, IEquipmentTransaction, IBusinessAuthorizer
    {
        private bool _open = true;
        private int _invoked;
        private string[] _grants = [];
        private string _plantId = "";
        private const int ListBatchSize = 128;
        private const string EquipmentColumns = "EQUIPMENT_ID AS Id, VERSION AS Version, CODE AS Code, NAME AS Name, "
            + "CAPACITY AS Capacity, REQUIRES_APPROVAL AS RequiresApproval, IS_ACTIVE AS Active";
        private const string BookingColumns = "BOOKING_ID AS Id, VERSION AS Version, EQUIPMENT_ID AS EquipmentId, EMPLOYEE_ID AS EmployeeId, "
            + "START_TICKS AS StartTicks, END_TICKS AS EndTicks, QUANTITY AS Quantity, REQUESTED_BY AS RequestedBy, STATE AS State, RETURNED_TICKS AS ReturnedTicks";
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
        internal async Task<T> Scalar<T>(string sql, object? values, CancellationToken ct)
            => (await connection.ExecuteScalarAsync<T>(Command(sql, values, ct)))!;
        internal async Task Write(string sql, object values, CancellationToken ct)
        {
            if (await connection.ExecuteAsync(Command(sql, values, ct)) != 1)
                throw new DBConcurrencyException("Shared-asset write did not affect exactly one row.");
        }
        internal async Task<string> RequireAdministrator(string userId, CancellationToken ct)
        {
            try { return await memberships.RequireAdministratorInTransactionAsync(transaction, userId, ct); }
            catch (UnauthorizedAccessException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
        }
        internal Task<string?> FindPlant(string plantId, CancellationToken ct) => masters.FindPlantAsync(transaction, plantId, ct);
        internal Task<CreationRow?> FindCreation(Guid operationId, CancellationToken ct)
            => connection.QuerySingleOrDefaultAsync<CreationRow>(Command("""
                SELECT EQUIPMENT_ID AS EquipmentId, REQUESTED_BY AS RequestedBy, CODE AS Code, NAME AS Name,
                       CAPACITY AS Capacity, REQUIRES_APPROVAL AS RequiresApproval
                  FROM IVT_SHARED_EQUIPMENT_CREATION WHERE
                """ + " " + ScopeWhere + " AND OPERATION_ID=@id", new { id = Text(operationId) }, ct));
        internal Task RecordCreation(Guid operationId, SharedEquipment value, CancellationToken ct)
            => Write("""
                INSERT INTO IVT_SHARED_EQUIPMENT_CREATION (TENANT_ID, ORGANIZATION_ID, OPERATION_ID, EQUIPMENT_ID,
                    REQUESTED_BY, CODE, NAME, CAPACITY, REQUIRES_APPROVAL)
                VALUES (@TenantId, @OrganizationId, @Operation, @Equipment, @Actor, @Code, @Name, @Capacity, @RequiresApproval)
                """, new { Operation = Text(operationId), Equipment = Text(value.Id), Actor = Actor.UserId,
                    value.Code, value.Name, value.Capacity, value.RequiresApproval }, ct);
        internal async Task<InventoryScopeBinding?> ReadBinding(CancellationToken ct)
        {
            var row = await connection.QuerySingleOrDefaultAsync<BindingRow>(Command(
                "SELECT PLANT_ID AS PlantId, VERSION AS Version, IS_ACTIVE AS Active FROM IVT_BUSINESS_SCOPE WHERE " + ScopeWhere, null, ct));
            return row is null ? null : new(Id(Scope.TenantId), Id(Scope.OrganizationId), row.PlantId, Id(row.Version), row.Active);
        }
        internal async Task Authorize(string userId, string permission, CancellationToken ct)
        {
            BusinessMembership? membership;
            try { membership = await memberships.GetAccessInTransactionAsync(transaction, userId, Id(Scope.TenantId), Id(Scope.OrganizationId), ct); }
            catch (InvalidDataException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
            var binding = await ReadBinding(ct);
            if (membership is null || !membership.Permissions.Contains(permission, StringComparer.Ordinal)
                || binding is null || !binding.Active || await FindPlant(binding.PlantId, ct) is null)
                throw Failure("BUSINESS_ACCESS_DENIED");
            Actor = new(Text(membership.BusinessUserId), Scope);
            _grants = membership.Permissions.ToArray(); _plantId = binding.PlantId;
        }
        public Task<bool> IsAllowedAsync(BusinessActor actor, string permission, string resourceType, Guid? resourceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_open && actor == Actor && _grants.Contains(permission, StringComparer.Ordinal));
        }
        public async Task<T> ExecuteAsync<T>(BusinessScope requestedScope, Func<IEquipmentTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            if (!_open || requestedScope != Scope) throw Failure("STORAGE_SCOPE_VIOLATION");
            if (Interlocked.Exchange(ref _invoked, 1) != 0) throw Failure("TRANSACTION_REPLAY_NOT_ALLOWED");
            ct.ThrowIfCancellationRequested();
            var value = await work(this, ct);
            ct.ThrowIfCancellationRequested();
            return value;
        }
        internal async Task<Guid> ResolveWorker(string workerId, CancellationToken ct)
        {
            if (!ValidKey(workerId)) throw Failure("INVALID_BUSINESS_INPUT");
            // Existing mappings also resolve historical retries after a worker is moved or deactivated.
            // Framework checks live plant membership before every new booking, approval or checkout.
            var existing = await Scalar<string?>("SELECT EMPLOYEE_ID FROM IVT_WORKER_IDENTITY WHERE WORKER_ID=@workerId", new { workerId }, ct);
            if (existing is not null) return Id(existing);
            var canonicalWorker = await masters.FindActiveWorkerAsync(transaction, workerId, _plantId, ct)
                ?? throw Failure("EMPLOYEE_NOT_FOUND");
            var id = Guid.NewGuid();
            await Write("INSERT INTO IVT_WORKER_IDENTITY (WORKER_ID, EMPLOYEE_ID) VALUES (@workerId, @id)", new { workerId = canonicalWorker, id = Text(id) }, ct);
            return id;
        }
        public async Task<bool> EmployeeExistsAsync(Guid employeeId, CancellationToken ct)
        {
            var worker = await Scalar<string?>("SELECT WORKER_ID FROM IVT_WORKER_IDENTITY WHERE EMPLOYEE_ID=@id", new { id = Text(employeeId) }, ct);
            return worker is not null && await masters.FindActiveWorkerAsync(transaction, worker, _plantId, ct) is not null;
        }

        public async Task<SharedEquipment?> FindEquipmentAsync(Guid id, CancellationToken ct)
        {
            var row = await connection.QuerySingleOrDefaultAsync<EquipmentRow>(Command("SELECT " + EquipmentColumns
                + " FROM IVT_SHARED_EQUIPMENT WHERE " + ScopeWhere + " AND EQUIPMENT_ID=@id", new { id = Text(id) }, ct));
            return row is null ? null : Equipment(row);
        }
        private SharedEquipment Equipment(EquipmentRow row)
            => new(Id(row.Id), Scope, Id(row.Version), row.Code, row.Name, row.Capacity, row.RequiresApproval, row.Active);

        public async Task<BusinessPage<SharedEquipment>> QueryEquipmentAsync(InventoryQuery query, CancellationToken ct)
        {
            // O(scope rows), bounded materialization: managed matching preserves literal ordinal-ignore-case
            // semantics across providers. Apply every filter before counting or retaining page items.
            var items = new List<SharedEquipment>(query.Limit);
            long total = 0;
            var end = (long)query.Offset + query.Limit;
            string? afterCode = null, afterId = null;
            while (true)
            {
                var rows = (await connection.QueryAsync<EquipmentRow>(Command("SELECT " + EquipmentColumns
                    + " FROM IVT_SHARED_EQUIPMENT WHERE " + ScopeWhere
                    + " AND (@IncludeInactive=1 OR IS_ACTIVE=1)"
                    + " AND (@AfterCode IS NULL OR CODE>@AfterCode OR (CODE=@AfterCode AND EQUIPMENT_ID>@AfterId))"
                    + " ORDER BY CODE, EQUIPMENT_ID" + batchLimitSql,
                    new { query.IncludeInactive, AfterCode = afterCode, AfterId = afterId, BatchSize = ListBatchSize }, ct))).ToArray();
                foreach (var row in rows)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!string.IsNullOrEmpty(query.Text) && !row.Code.Contains(query.Text, StringComparison.OrdinalIgnoreCase)
                        && !row.Name.Contains(query.Text, StringComparison.OrdinalIgnoreCase)) continue;
                    if (total >= query.Offset && total < end) items.Add(Equipment(row));
                    total++;
                }
                if (rows.Length < ListBatchSize) break;
                afterCode = rows[^1].Code; afterId = rows[^1].Id;
            }
            return new(Array.AsReadOnly(items.ToArray()), total);
        }
        public async Task<bool> HasOpenEquipmentBookingsAsync(Guid id, CancellationToken ct)
            => await Scalar<long>("SELECT COUNT(*) FROM IVT_SHARED_BOOKING WHERE " + ScopeWhere
                + " AND EQUIPMENT_ID=@id AND STATE IN (0,1,2)", new { id = Text(id) }, ct) != 0;
        public async Task SaveEquipmentAsync(SharedEquipment value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            RequireCode(value.Code);
            var duplicate = await Scalar<string?>("SELECT EQUIPMENT_ID FROM IVT_SHARED_EQUIPMENT WHERE " + ScopeWhere
                + " AND CODE=@Code", new { value.Code }, ct);
            if (duplicate is not null && Id(duplicate) != value.Id) throw Failure("EQUIPMENT_CODE_CONFLICT");
            await Write(expectedVersion is null ? """
                INSERT INTO IVT_SHARED_EQUIPMENT (TENANT_ID, ORGANIZATION_ID, EQUIPMENT_ID, VERSION, CODE, NAME, CAPACITY, REQUIRES_APPROVAL, IS_ACTIVE)
                VALUES (@TenantId, @OrganizationId, @Id, @Version, @Code, @Name, @Capacity, @RequiresApproval, @Active)
                """ : """
                UPDATE IVT_SHARED_EQUIPMENT SET VERSION=@Version, CODE=@Code, NAME=@Name, CAPACITY=@Capacity,
                    REQUIRES_APPROVAL=@RequiresApproval, IS_ACTIVE=@Active WHERE
                """ + " " + ScopeWhere + " AND EQUIPMENT_ID=@Id AND VERSION=@Previous",
                new { Id = Text(value.Id), Version = Text(value.Version), value.Code, value.Name, value.Capacity, value.RequiresApproval,
                    value.Active, Previous = expectedVersion.HasValue ? Text(expectedVersion.Value) : null }, ct);
        }
        public async Task<bool> HasEquipmentCapacityAsync(Guid equipmentId, DateTimeOffset start, DateTimeOffset end,
            int quantity, Guid? exceptId, CancellationToken ct)
        {
            var equipment = await FindEquipmentAsync(equipmentId, ct) ?? throw Failure("EQUIPMENT_NOT_FOUND");
            // Sum deltas grouped at each boundary: touching intervals release before the next starts.
            // A checked-out row has no effective end until a return is actually stored.
            var used = await Scalar<long>("""
                WITH intervals AS (
                    SELECT CASE WHEN START_TICKS<@Start THEN @Start ELSE START_TICKS END AS BeginAt,
                           CASE WHEN STATE=2 OR END_TICKS>@End THEN @End ELSE END_TICKS END AS EndAt,
                           CAST(QUANTITY AS BIGINT) AS Quantity
                      FROM IVT_SHARED_BOOKING
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND EQUIPMENT_ID=@Id
                       AND STATE IN (0,1,2) AND START_TICKS<@End AND (STATE=2 OR END_TICKS>@Start)
                       AND (@ExceptId IS NULL OR BOOKING_ID<>@ExceptId)
                ), points AS (
                    SELECT BeginAt AS PointTicks, Quantity AS Delta FROM intervals
                    UNION ALL SELECT EndAt AS PointTicks, -Quantity AS Delta FROM intervals
                ), changes AS (SELECT PointTicks, SUM(Delta) AS Delta FROM points GROUP BY PointTicks),
                levels AS (SELECT SUM(Delta) OVER (ORDER BY PointTicks ROWS UNBOUNDED PRECEDING) AS Used FROM changes)
                SELECT COALESCE(MAX(Used),0) FROM levels
                """, new { Id = Text(equipmentId), Start = start.UtcTicks, End = end.UtcTicks,
                    ExceptId = exceptId.HasValue ? Text(exceptId.Value) : null }, ct);
            return quantity > 0 && used <= (long)equipment.Capacity - quantity;
        }
        public async Task<EquipmentBooking?> FindBookingAsync(Guid id, CancellationToken ct)
        {
            var row = await connection.QuerySingleOrDefaultAsync<BookingRow>(Command("SELECT " + BookingColumns
                + " FROM IVT_SHARED_BOOKING WHERE " + ScopeWhere + " AND BOOKING_ID=@id", new { id = Text(id) }, ct));
            return row is null ? null : Booking(row);
        }
        private EquipmentBooking Booking(BookingRow row)
            => new(Id(row.Id), Scope, Id(row.Version), Id(row.EquipmentId), Id(row.EmployeeId),
                new(row.StartTicks, TimeSpan.Zero), new(row.EndTicks, TimeSpan.Zero), row.Quantity, Text(Id(row.RequestedBy)),
                (EquipmentBookingState)row.State, row.ReturnedTicks.HasValue ? new(row.ReturnedTicks.Value, TimeSpan.Zero) : null);

        public async Task<BusinessPage<EquipmentBooking>> QueryBookingsAsync(Guid? equipmentId, EquipmentBookingState? state,
            int offset, int limit, CancellationToken ct)
        {
            // Historical bookings remain readable without consulting current worker or equipment eligibility.
            var where = ScopeWhere + " AND (@EquipmentId IS NULL OR EQUIPMENT_ID=@EquipmentId) AND (@State IS NULL OR STATE=@State)";
            var values = new { EquipmentId = equipmentId.HasValue ? Text(equipmentId.Value) : null,
                State = state.HasValue ? (int?)state.Value : null, Offset = (long)offset, End = (long)offset + limit };
            // SUM(BIGINT) supplies a 64-bit total on both SQL Server and SQLite.
            var total = await Scalar<long>("SELECT COALESCE(SUM(CAST(1 AS BIGINT)),0) FROM IVT_SHARED_BOOKING WHERE " + where, values, ct);
            var rows = await connection.QueryAsync<BookingRow>(Command("SELECT * FROM (SELECT " + BookingColumns
                + ", ROW_NUMBER() OVER (ORDER BY START_TICKS, BOOKING_ID) AS RowNumber FROM IVT_SHARED_BOOKING WHERE " + where
                + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber", values, ct));
            return new(Array.AsReadOnly(rows.Select(Booking).ToArray()), total);
        }
        public Task SaveBookingAsync(EquipmentBooking value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            return Write(expectedVersion is null ? """
                INSERT INTO IVT_SHARED_BOOKING (TENANT_ID, ORGANIZATION_ID, BOOKING_ID, VERSION, EQUIPMENT_ID, EMPLOYEE_ID,
                    START_TICKS, END_TICKS, QUANTITY, REQUESTED_BY, STATE, RETURNED_TICKS)
                VALUES (@TenantId, @OrganizationId, @Id, @Version, @EquipmentId, @EmployeeId,
                    @StartTicks, @EndTicks, @Quantity, @RequestedBy, @State, @ReturnedTicks)
                """ : """
                UPDATE IVT_SHARED_BOOKING SET VERSION=@Version, STATE=@State, RETURNED_TICKS=@ReturnedTicks WHERE
                """ + " " + ScopeWhere + " " + """
                 AND BOOKING_ID=@Id AND VERSION=@Previous AND EQUIPMENT_ID=@EquipmentId AND EMPLOYEE_ID=@EmployeeId
                 AND START_TICKS=@StartTicks AND END_TICKS=@EndTicks AND QUANTITY=@Quantity AND REQUESTED_BY=@RequestedBy
                """, new { Id = Text(value.Id), Version = Text(value.Version), EquipmentId = Text(value.EquipmentId), EmployeeId = Text(value.EmployeeId),
                    StartTicks = value.Start.UtcTicks, EndTicks = value.End.UtcTicks, value.Quantity, value.RequestedBy,
                    State = (int)value.State, ReturnedTicks = value.ReturnedAt?.UtcTicks, Previous = expectedVersion.HasValue ? Text(expectedVersion.Value) : null }, ct);
        }
        public Task AppendAuditAsync(BusinessAudit audit, CancellationToken ct)
        {
            if (audit.Actor != Actor) throw Failure("STORAGE_SCOPE_VIOLATION");
            return Write("""
                INSERT INTO IVT_SHARED_EQUIPMENT_AUDIT (TENANT_ID, ORGANIZATION_ID, AUDIT_ID, USER_ID, RESOURCE_TYPE,
                    RESOURCE_ID, OPERATION, PREVIOUS_VERSION, VERSION, AT_TICKS)
                VALUES (@TenantId, @OrganizationId, @Id, @UserId, @ResourceType, @ResourceId, @Operation, @Previous, @Version, @At)
                """, new { Id = Text(audit.Id), audit.Actor.UserId, audit.ResourceType, ResourceId = Text(audit.ResourceId), audit.Operation,
                    Previous = audit.PreviousVersion.HasValue ? Text(audit.PreviousVersion.Value) : null, Version = Text(audit.Version), At = audit.At.UtcTicks }, ct);
        }
        private void RequireScope(BusinessScope value) { if (value != Scope) throw Failure("STORAGE_SCOPE_VIOLATION"); }
    }

    private sealed class BindingRow { public string PlantId { get; set; } = ""; public string Version { get; set; } = ""; public bool Active { get; set; } }
    private sealed class EquipmentRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int Capacity { get; set; }
        public bool RequiresApproval { get; set; }
        public bool Active { get; set; }
    }
    private sealed class CreationRow
    {
        public string EquipmentId { get; set; } = "";
        public string RequestedBy { get; set; } = "";
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public int Capacity { get; set; }
        public bool RequiresApproval { get; set; }
    }
    private sealed class BookingRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string EmployeeId { get; set; } = "";
        public long StartTicks { get; set; }
        public long EndTicks { get; set; }
        public int Quantity { get; set; }
        public string RequestedBy { get; set; } = "";
        public int State { get; set; }
        public long? ReturnedTicks { get; set; }
    }
}
