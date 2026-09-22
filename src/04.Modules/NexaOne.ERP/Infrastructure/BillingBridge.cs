using System.Data;
using System.Data.Common;
using System.Globalization;
using Dapper;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP.Infrastructure;

/// <summary>Owns billing persistence and explicit customer enrollment. Document, line, number, payment and
/// audit rows of one operation share one Serializable commit; scope is SYS membership only.</summary>
public sealed class BillingBridge : IBillingBridge
{
    private const string ScopeWhere = "TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId";
    private readonly ServiceObjectProcessor _processor;
    private readonly int? _timeout;
    private readonly TimeProvider _clock;
    private readonly IBusinessMembershipBridge _memberships;
    private readonly IBusinessMasterDirectory _masters;

    public BillingBridge(EesDataSource dataSource, IBusinessMembershipBridge memberships,
        IBusinessMasterDirectory masters, TimeProvider? clock = null)
    {
        _processor = new(dataSource);
        _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        _clock = clock ?? TimeProvider.System;
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _masters = masters ?? throw new ArgumentNullException(nameof(masters));
    }

    public Task<BusinessPage<BusinessMembership>> ListAccessibleScopesAsync(string userId, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!ValidText(userId, 50)) throw Failure("BUSINESS_ACCESS_DENIED");
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            const int batchSize = 128;
            var items = new List<BusinessMembership>(limit);
            long total = 0;
            Guid? afterTenant = null, afterOrganization = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<BusinessMembership> batch;
                try { batch = await _memberships.ListAccessInTransactionAsync(transaction, userId, afterTenant, afterOrganization, batchSize, ct); }
                catch (InvalidDataException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
                if (batch is null || batch.Count > batchSize) throw Failure("STORAGE_CONTRACT_VIOLATION");
                foreach (var membership in batch)
                {
                    if (membership is null || membership.TenantId == Guid.Empty || membership.OrganizationId == Guid.Empty
                        || membership.BusinessUserId == Guid.Empty || !membership.IsActive || membership.Version <= 0 || membership.Permissions is null)
                        throw Failure("STORAGE_CONTRACT_VIOLATION");
                    // The owner uses canonical GUID text for its keyset order on both SQL providers.
                    if (afterTenant.HasValue)
                    {
                        var tenantOrder = string.CompareOrdinal(Text(membership.TenantId), Text(afterTenant.Value));
                        if (tenantOrder < 0 || tenantOrder == 0 && string.CompareOrdinal(Text(membership.OrganizationId), Text(afterOrganization!.Value)) <= 0)
                            throw Failure("STORAGE_CONTRACT_VIOLATION");
                    }
                    afterTenant = membership.TenantId; afterOrganization = membership.OrganizationId;
                    if (!membership.Permissions.Any(grant => grant.StartsWith("billing.", StringComparison.Ordinal))) continue;
                    if (total++ >= offset && items.Count < limit) items.Add(membership);
                }
                if (batch.Count < batchSize) break;
            }
            ct.ThrowIfCancellationRequested();
            return new BusinessPage<BusinessMembership>(Array.AsReadOnly(items.ToArray()), total);
        }, IsolationLevel.Serializable, ct);
    }
    public Task<BillingContact> EnrollContactAsync(string userId, Guid tenantId, Guid organizationId, string customerId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write", (_, session) => session.Enroll(customerId, ct), ct);
    public Task<BusinessPage<BillingContact>> ListContactsAsync(string userId, Guid tenantId, Guid organizationId,
        int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return Run(userId, tenantId, organizationId, "billing.read", (_, session) => session.Contacts(offset, limit, ct), ct);
    }
    public Task<BillingDocument> CreateDocumentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, BillingKind kind, BillingDocumentInput input, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write", (service, session) => service.CreateDocumentAsync(session.Actor, operationId, kind, input, ct), ct);
    public Task<BillingDocument> UpdateDocumentAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, BillingDocumentInput input, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write", (service, session) => service.UpdateDocumentAsync(session.Actor, id, version, input, ct), ct);
    public Task<BillingDocument> GetDocumentAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.read", (service, session) => service.GetDocumentAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<BillingDocument>> ListDocumentsAsync(string userId, Guid tenantId, Guid organizationId,
        BillingKind? kind = null, BillingStatus? status = null, Guid? contactId = null, int offset = 0, int limit = 50, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.read", (service, session) => service.ListDocumentsAsync(session.Actor, kind, status, contactId, offset, limit, ct), ct);
    public Task<BillingDocument> MarkSentAsync(string userId, Guid tenantId, Guid organizationId, Guid id, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write", (service, session) => service.MarkSentAsync(session.Actor, id, version, ct), ct);
    public Task<BillingDocument> DecideEstimateAsync(string userId, Guid tenantId, Guid organizationId, Guid id, Guid version, bool accepted, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.decide", (service, session) => service.DecideEstimateAsync(session.Actor, id, version, accepted, ct), ct);
    public Task<BillingDocument> ConvertEstimateAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid estimateId, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write", (service, session) => service.ConvertEstimateAsync(session.Actor, operationId, estimateId, version, ct), ct);
    public Task<BillingDocument> VoidDocumentAsync(string userId, Guid tenantId, Guid organizationId, Guid id, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write", (service, session) => service.VoidDocumentAsync(session.Actor, id, version, ct), ct);
    public Task<PaymentRecord> RecordPaymentAsync(string userId, Guid tenantId, Guid organizationId, Guid operationId, PaymentInput input, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.pay", (service, session) => service.RecordPaymentAsync(session.Actor, operationId, input, ct), ct);
    public Task<PaymentRecord> CancelPaymentAsync(string userId, Guid tenantId, Guid organizationId, Guid id, Guid version, string? reason, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.pay", (service, session) => service.CancelPaymentAsync(session.Actor, id, version, reason, ct), ct);
    public Task<PaymentRecord> GetPaymentAsync(string userId, Guid tenantId, Guid organizationId, Guid id, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.read", (service, session) => service.GetPaymentAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<PaymentRecord>> ListPaymentsAsync(string userId, Guid tenantId, Guid organizationId,
        Guid documentId, PaymentState? state = null, int offset = 0, int limit = 50, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.read", (service, session) => service.ListPaymentsAsync(session.Actor, documentId, state, offset, limit, ct), ct);

    private Task<T> Run<T>(string userId, Guid tenantId, Guid organizationId, string permission,
        Func<BillingService, Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty) throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout, new("NexaOne.MES", Text(tenantId), Text(organizationId)),
                _memberships, _masters, _clock);
            try
            {
                await session.Authorize(userId, permission, ct);
                var result = await action(new BillingService(session, session, _clock), session);
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
        ? id : throw new InvalidDataException("Billing storage contains an invalid identity or version.");
    private static Guid? OptionalId(string? value) => value is null ? null : Id(value);
    // TEXT avoids SQLite NUMERIC affinity converting large or fractional values to binary floating point.
    // Canonical plain decimals keep equality checks provider independent.
    private static string Amount(decimal value)
    {
        if (decimal.Round(value, 6) != value) throw Failure("STORAGE_CONTRACT_VIOLATION");
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }
    private static decimal Amount(string value)
        => decimal.TryParse(value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var result) && Amount(result) == value
            ? result : throw new InvalidDataException("Billing storage contains a noncanonical amount.");
    private static string? Amount(decimal? value) => value.HasValue ? Amount(value.Value) : null;
    private static string Day(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static DateOnly Day(string value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) && Day(day) == value
            ? day : throw new InvalidDataException("Billing storage contains a noncanonical date.");
    private static BillingAdjustment? Adjustment(int? type, string? value)
        => type.HasValue != (value is not null) ? throw new InvalidDataException("Billing storage contains a partial adjustment.")
            : type.HasValue ? new((BillingAdjustmentType)type.Value, Amount(value!)) : null;

    // Confined to one processor-owned transaction. This adapter never commits, retries or caches authority.
    private sealed class Session(DbConnection connection, DbTransaction transaction, int? timeout, BusinessScope scope,
        IBusinessMembershipBridge memberships, IBusinessMasterDirectory masters, TimeProvider clock)
        : IAtomicBusinessStore<IBillingTransaction>, IBillingTransaction, IBusinessAuthorizer
    {
        private bool _open = true;
        private int _invoked;
        private string[] _grants = [];
        private const string DocumentColumns = "DOCUMENT_ID AS Id, VERSION AS Version, OPERATION_ID AS OperationId, KIND AS Kind, NUMBER AS Number, "
            + "STATUS AS Status, CONTACT_ID AS ContactId, DOCUMENT_DATE AS DocumentDate, DUE_DATE AS DueDate, CURRENCY AS Currency, "
            + "DISCOUNT_TYPE AS DiscountType, DISCOUNT_VALUE AS DiscountValue, TAX_TYPE AS TaxType, TAX_VALUE AS TaxValue, TAX2_TYPE AS Tax2Type, TAX2_VALUE AS Tax2Value, "
            + "TERMS AS Terms, NOTE AS Note, SUBTOTAL AS Subtotal, DISCOUNT_AMOUNT AS DiscountAmount, TAX_AMOUNT AS TaxAmount, TOTAL AS Total, PAID AS Paid, "
            + "CREATED_BY AS CreatedBy, CONVERTED_FROM_ID AS ConvertedFrom, CONVERTED_TO_ID AS ConvertedTo";
        private const string PaymentColumns = "PAYMENT_ID AS Id, VERSION AS Version, OPERATION_ID AS OperationId, DOCUMENT_ID AS DocumentId, AMOUNT AS Amount, "
            + "CURRENCY AS Currency, PAID_AT_TICKS AS PaidAt, METHOD AS Method, REFERENCE AS Reference, NOTE AS Note, CREATED_BY AS CreatedBy, STATE AS State, "
            + "CANCELLED_BY AS CancelledBy, CANCELLED_AT_TICKS AS CancelledAt, CANCEL_REASON AS CancelReason";
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
        private async Task<T[]> Rows<T>(string sql, object? values, CancellationToken ct)
            => (await connection.QueryAsync<T>(Command(sql, values, ct))).ToArray();
        private async Task<T> Scalar<T>(string sql, object? values, CancellationToken ct)
            => (await connection.ExecuteScalarAsync<T>(Command(sql, values, ct)))!;
        private Task<int> Execute(string sql, object? values, CancellationToken ct) => connection.ExecuteAsync(Command(sql, values, ct));
        private async Task Write(string sql, object values, CancellationToken ct)
        {
            if (await Execute(sql, values, ct) != 1) throw new DBConcurrencyException("Billing write did not affect exactly one row.");
        }
        internal async Task Authorize(string userId, string permission, CancellationToken ct)
        {
            BusinessMembership? membership;
            try { membership = await memberships.GetAccessInTransactionAsync(transaction, userId, Id(Scope.TenantId), Id(Scope.OrganizationId), ct); }
            catch (InvalidDataException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
            if (membership is null || !membership.Permissions.Contains(permission, StringComparer.Ordinal)) throw Failure("BUSINESS_ACCESS_DENIED");
            Actor = new(Text(membership.BusinessUserId), Scope); _grants = membership.Permissions.ToArray();
        }
        public Task<bool> IsAllowedAsync(BusinessActor actor, string permission, string resourceType, Guid? resourceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_open && actor == Actor && _grants.Contains(permission, StringComparer.Ordinal));
        }
        public async Task<T> ExecuteAsync<T>(BusinessScope requestedScope, Func<IBillingTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            if (!_open || requestedScope != Scope) throw Failure("STORAGE_SCOPE_VIOLATION");
            if (Interlocked.Exchange(ref _invoked, 1) != 0) throw Failure("TRANSACTION_REPLAY_NOT_ALLOWED");
            ct.ThrowIfCancellationRequested(); var result = await work(this, ct); ct.ThrowIfCancellationRequested(); return result;
        }

        // Contacts: an enrolled MDM customer. Name and activity come from the master row at read time.
        private Task<ContactRow?> ContactRow(string predicate, string key, CancellationToken ct)
            => Row<ContactRow>("SELECT CONTACT_ID AS Id, VERSION AS Version, MASTER_CUSTOMER_ID AS CustomerId FROM ERP_BILLING_CONTACT WHERE "
                + ScopeWhere + " AND " + predicate, new { key }, ct);
        internal async Task<BillingContact> Enroll(string customerId, CancellationToken ct)
        {
            RequireText(customerId, 50);
            var master = await masters.FindCustomerAsync(transaction, customerId, ct) ?? throw Failure("CUSTOMER_NOT_FOUND");
            if (!master.IsActive) throw Failure("CUSTOMER_INACTIVE");
            var current = await ContactRow("MASTER_CUSTOMER_ID=@key", master.CustomerId, ct);
            if (current is not null) return new(Id(current.Id), Id(current.Version), master.CustomerId, master.CustomerName, master.IsActive);
            var id = Guid.NewGuid(); var version = Guid.NewGuid();
            await Write("INSERT INTO ERP_BILLING_CONTACT (TENANT_ID, ORGANIZATION_ID, CONTACT_ID, VERSION, MASTER_CUSTOMER_ID) VALUES (@TenantId, @OrganizationId, @Id, @Version, @Master)",
                new { Id = Text(id), Version = Text(version), Master = master.CustomerId }, ct);
            await AppendAuditAsync(new(Guid.NewGuid(), Actor, "billing-contact", id, "enrolled", null, version, clock.GetUtcNow()), ct);
            return new(id, version, master.CustomerId, master.CustomerName, master.IsActive);
        }
        internal async Task<BusinessPage<BillingContact>> Contacts(int offset, int limit, CancellationToken ct)
        {
            var values = new { Offset = offset, End = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_CONTACT WHERE " + ScopeWhere, null, ct);
            var rows = await Rows<ContactRow>("SELECT * FROM (SELECT CONTACT_ID AS Id, VERSION AS Version, MASTER_CUSTOMER_ID AS CustomerId, "
                + "ROW_NUMBER() OVER (ORDER BY MASTER_CUSTOMER_ID, CONTACT_ID) AS RowNumber FROM ERP_BILLING_CONTACT WHERE " + ScopeWhere
                + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber", values, ct);
            if (rows.Length == 0) return new(Array.Empty<BillingContact>(), total);
            var found = await masters.FindCustomersAsync(transaction, rows.Select(row => row.CustomerId).ToArray(), ct);
            var byId = new Dictionary<string, CustomerDto>(StringComparer.Ordinal);
            foreach (var master in found)
                if (master?.CustomerId is null || master.CustomerName is null || !byId.TryAdd(master.CustomerId, master)) throw Failure("STORAGE_CONTRACT_VIOLATION");
            var items = rows.Select(row => byId.TryGetValue(row.CustomerId, out var master)
                ? new BillingContact(Id(row.Id), Id(row.Version), master.CustomerId, master.CustomerName, master.IsActive)
                : throw Failure("STORAGE_CONTRACT_VIOLATION")).ToArray();
            return new(Array.AsReadOnly(items), total);
        }
        public async Task<bool> ContactExistsAsync(Guid contactId, CancellationToken ct)
            => await ContactRow("CONTACT_ID=@key", Text(contactId), ct) is not null;

        public async Task<long> NextNumberAsync(BillingKind kind, CancellationToken ct)
        {
            // Counter row per scope and kind, advanced in the document's own transaction. A first document
            // created concurrently in two transactions may deadlock on SQL Server and surfaces as a storage failure.
            var values = new { Kind = (int)kind };
            if (await Execute("UPDATE ERP_BILLING_NUMBER SET NEXT_NUMBER=NEXT_NUMBER+1 WHERE " + ScopeWhere + " AND KIND=@Kind", values, ct) == 0)
            {
                await Write("INSERT INTO ERP_BILLING_NUMBER (TENANT_ID, ORGANIZATION_ID, KIND, NEXT_NUMBER) VALUES (@TenantId, @OrganizationId, @Kind, 1)", values, ct);
                return 1;
            }
            return await Scalar<long>("SELECT NEXT_NUMBER FROM ERP_BILLING_NUMBER WHERE " + ScopeWhere + " AND KIND=@Kind", values, ct);
        }

        // Documents: header row plus ordered lines, rebuilt into the Framework record on every read.
        private async Task<BillingDocument?> ReadDocument(string predicate, Guid key, CancellationToken ct)
        {
            var row = await Row<DocumentRow>("SELECT " + DocumentColumns + " FROM ERP_BILLING_DOCUMENT WHERE " + ScopeWhere + " AND " + predicate, new { key = Text(key) }, ct);
            if (row is null) return null;
            var lines = await Lines([row.Id], ct);
            return Document(row, lines.TryGetValue(row.Id, out var own) ? own : []);
        }
        private async Task<Dictionary<string, List<BillingLine>>> Lines(string[] documentIds, CancellationToken ct)
        {
            var rows = await Rows<LineRow>("SELECT DOCUMENT_ID AS DocumentId, LINE_NO AS LineNumber, DESCRIPTION AS Description, UNIT_PRICE AS UnitPrice, "
                + "QUANTITY AS Quantity, APPLY_TAX AS ApplyTax, APPLY_DISCOUNT AS ApplyDiscount FROM ERP_BILLING_LINE WHERE " + ScopeWhere
                + " AND DOCUMENT_ID IN @Ids ORDER BY DOCUMENT_ID, LINE_NO", new { Ids = documentIds }, ct);
            var result = new Dictionary<string, List<BillingLine>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.DocumentId, out var list)) result[row.DocumentId] = list = [];
                if (row.LineNumber != list.Count + 1) throw new InvalidDataException("Billing storage contains a gap in document lines.");
                list.Add(new(row.Description, Amount(row.UnitPrice), Amount(row.Quantity), row.ApplyTax, row.ApplyDiscount));
            }
            return result;
        }
        private BillingDocument Document(DocumentRow row, List<BillingLine> lines)
            => new(Id(row.Id), Scope, Id(row.Version), Id(row.OperationId), (BillingKind)row.Kind, row.Number,
                new(Id(row.ContactId), Day(row.DocumentDate), Day(row.DueDate), row.Currency, Array.AsReadOnly(lines.ToArray()),
                    Adjustment(row.DiscountType, row.DiscountValue), Adjustment(row.TaxType, row.TaxValue), Adjustment(row.Tax2Type, row.Tax2Value), row.Terms, row.Note),
                new(Amount(row.Subtotal), Amount(row.DiscountAmount), Amount(row.TaxAmount), Amount(row.Total)), (BillingStatus)row.Status,
                Text(Id(row.CreatedBy)), Amount(row.Paid), OptionalId(row.ConvertedFrom), OptionalId(row.ConvertedTo));
        public Task<BillingDocument?> FindDocumentAsync(Guid id, CancellationToken ct) => ReadDocument("DOCUMENT_ID=@key", id, ct);
        public Task<BillingDocument?> FindDocumentByOperationAsync(Guid operationId, CancellationToken ct) => ReadDocument("OPERATION_ID=@key", operationId, ct);
        public async Task SaveDocumentAsync(BillingDocument value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            var input = value.Input;
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId), Kind = (int)value.Kind, value.Number, Status = (int)value.Status,
                Contact = Text(input.ContactId), DocumentDate = Day(input.DocumentDate), DueDate = Day(input.DueDate), input.Currency,
                DiscountType = (int?)input.Discount?.Type, DiscountValue = Amount(input.Discount?.Value), TaxType = (int?)input.Tax?.Type, TaxValue = Amount(input.Tax?.Value),
                Tax2Type = (int?)input.Tax2?.Type, Tax2Value = Amount(input.Tax2?.Value), input.Terms, input.Note,
                Subtotal = Amount(value.Totals.Subtotal), DiscountAmount = Amount(value.Totals.DiscountAmount), TaxAmount = Amount(value.Totals.TaxAmount),
                Total = Amount(value.Totals.Total), Paid = Amount(value.Paid), value.CreatedBy, ConvertedFrom = Text(value.ConvertedFromId), ConvertedTo = Text(value.ConvertedToId),
                Previous = Text(expectedVersion), At = clock.GetUtcNow().UtcTicks
            };
            if (expectedVersion is null)
            {
                await Write("""
                    INSERT INTO ERP_BILLING_DOCUMENT (TENANT_ID, ORGANIZATION_ID, DOCUMENT_ID, VERSION, OPERATION_ID, KIND, NUMBER, STATUS, CONTACT_ID,
                        DOCUMENT_DATE, DUE_DATE, CURRENCY, DISCOUNT_TYPE, DISCOUNT_VALUE, TAX_TYPE, TAX_VALUE, TAX2_TYPE, TAX2_VALUE, TERMS, NOTE,
                        SUBTOTAL, DISCOUNT_AMOUNT, TAX_AMOUNT, TOTAL, PAID, CREATED_BY, CONVERTED_FROM_ID, CONVERTED_TO_ID, AT_TICKS)
                    VALUES (@TenantId, @OrganizationId, @Id, @Version, @Operation, @Kind, @Number, @Status, @Contact,
                        @DocumentDate, @DueDate, @Currency, @DiscountType, @DiscountValue, @TaxType, @TaxValue, @Tax2Type, @Tax2Value, @Terms, @Note,
                        @Subtotal, @DiscountAmount, @TaxAmount, @Total, @Paid, @CreatedBy, @ConvertedFrom, @ConvertedTo, @At)
                    """, values, ct);
                await WriteLines(value.Id, input.Lines, ct);
                return;
            }
            // Kind, number, operation, creator and conversion source never change once written.
            var current = await FindDocumentAsync(value.Id, ct) ?? throw Failure("BILLING_DOCUMENT_NOT_FOUND");
            if (current.Version != expectedVersion) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (current.Kind != value.Kind || current.Number != value.Number || current.OperationId != value.OperationId
                || current.CreatedBy != value.CreatedBy || current.ConvertedFromId != value.ConvertedFromId
                || (current.ConvertedToId is not null && current.ConvertedToId != value.ConvertedToId))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            await Write("UPDATE ERP_BILLING_DOCUMENT SET VERSION=@Version, STATUS=@Status, CONTACT_ID=@Contact, DOCUMENT_DATE=@DocumentDate, DUE_DATE=@DueDate, "
                + "CURRENCY=@Currency, DISCOUNT_TYPE=@DiscountType, DISCOUNT_VALUE=@DiscountValue, TAX_TYPE=@TaxType, TAX_VALUE=@TaxValue, TAX2_TYPE=@Tax2Type, "
                + "TAX2_VALUE=@Tax2Value, TERMS=@Terms, NOTE=@Note, SUBTOTAL=@Subtotal, DISCOUNT_AMOUNT=@DiscountAmount, TAX_AMOUNT=@TaxAmount, TOTAL=@Total, "
                + "PAID=@Paid, CONVERTED_TO_ID=@ConvertedTo WHERE " + ScopeWhere + " AND DOCUMENT_ID=@Id AND VERSION=@Previous", values, ct);
            if (!current.Input.Lines.SequenceEqual(input.Lines))
            {
                await Execute("DELETE FROM ERP_BILLING_LINE WHERE " + ScopeWhere + " AND DOCUMENT_ID=@Id", new { Id = Text(value.Id) }, ct);
                await WriteLines(value.Id, input.Lines, ct);
            }
        }
        private async Task WriteLines(Guid documentId, IReadOnlyList<BillingLine> lines, CancellationToken ct)
        {
            for (var index = 0; index < lines.Count; index++)
                await Write("INSERT INTO ERP_BILLING_LINE (TENANT_ID, ORGANIZATION_ID, DOCUMENT_ID, LINE_NO, DESCRIPTION, UNIT_PRICE, QUANTITY, APPLY_TAX, APPLY_DISCOUNT) "
                    + "VALUES (@TenantId, @OrganizationId, @Id, @LineNumber, @Description, @UnitPrice, @Quantity, @ApplyTax, @ApplyDiscount)",
                    new { Id = Text(documentId), LineNumber = index + 1, lines[index].Description, UnitPrice = Amount(lines[index].UnitPrice),
                        Quantity = Amount(lines[index].Quantity), lines[index].ApplyTax, lines[index].ApplyDiscount }, ct);
        }
        public async Task<BusinessPage<BillingDocument>> QueryDocumentsAsync(BillingKind? kind, BillingStatus? status, Guid? contactId, int offset, int limit, CancellationToken ct)
        {
            // Optional filters are parameterized and null-skipped; count and page share one filter in the same transaction.
            const string filter = " AND (@Kind IS NULL OR KIND=@Kind) AND (@Status IS NULL OR STATUS=@Status) AND (@Contact IS NULL OR CONTACT_ID=@Contact)";
            var values = new
            {
                Kind = kind.HasValue ? (int?)kind.Value : null, Status = status.HasValue ? (int?)status.Value : null,
                Contact = contactId.HasValue ? Text(contactId.Value) : null, Offset = offset, End = (long)offset + limit
            };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_DOCUMENT WHERE " + ScopeWhere + filter, values, ct);
            var rows = await Rows<DocumentRow>("SELECT * FROM (SELECT " + DocumentColumns + ", ROW_NUMBER() OVER (ORDER BY KIND, NUMBER) AS RowNumber "
                + "FROM ERP_BILLING_DOCUMENT WHERE " + ScopeWhere + filter + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber", values, ct);
            var lines = rows.Length == 0 ? new Dictionary<string, List<BillingLine>>(StringComparer.Ordinal) : await Lines(rows.Select(row => row.Id).ToArray(), ct);
            return new(Array.AsReadOnly(rows.Select(row => Document(row, lines.TryGetValue(row.Id, out var own) ? own : [])).ToArray()), total);
        }

        // Payments: one row per operation; cancellation keeps the row and fills the cancel columns.
        private PaymentRecord Payment(PaymentRow row)
            => new(Id(row.Id), Scope, Id(row.Version), Id(row.OperationId),
                new(Id(row.DocumentId), Amount(row.Amount), row.Currency, new DateTimeOffset(row.PaidAt, TimeSpan.Zero), (PaymentMethod)row.Method, row.Reference, row.Note),
                Text(Id(row.CreatedBy)), (PaymentState)row.State, row.CancelledBy is null ? null : Text(Id(row.CancelledBy)),
                row.CancelledAt.HasValue ? new DateTimeOffset(row.CancelledAt.Value, TimeSpan.Zero) : null, row.CancelReason);
        private async Task<PaymentRecord?> ReadPayment(string predicate, Guid key, CancellationToken ct)
        {
            var row = await Row<PaymentRow>("SELECT " + PaymentColumns + " FROM ERP_BILLING_PAYMENT WHERE " + ScopeWhere + " AND " + predicate, new { key = Text(key) }, ct);
            return row is null ? null : Payment(row);
        }
        public Task<PaymentRecord?> FindPaymentAsync(Guid id, CancellationToken ct) => ReadPayment("PAYMENT_ID=@key", id, ct);
        public Task<PaymentRecord?> FindPaymentByOperationAsync(Guid operationId, CancellationToken ct) => ReadPayment("OPERATION_ID=@key", operationId, ct);
        public async Task SavePaymentAsync(PaymentRecord value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId), Document = Text(value.Input.DocumentId),
                Amount = Amount(value.Input.Amount), value.Input.Currency, PaidAt = value.Input.PaidAt.UtcTicks, Method = (int)value.Input.Method,
                value.Input.Reference, value.Input.Note, value.CreatedBy, State = (int)value.State, value.CancelledBy,
                CancelledAt = value.CancelledAt?.UtcTicks, value.CancelReason, Previous = Text(expectedVersion)
            };
            if (expectedVersion is null)
            {
                await Write("""
                    INSERT INTO ERP_BILLING_PAYMENT (TENANT_ID, ORGANIZATION_ID, PAYMENT_ID, VERSION, OPERATION_ID, DOCUMENT_ID, AMOUNT, CURRENCY, PAID_AT_TICKS,
                        METHOD, REFERENCE, NOTE, CREATED_BY, STATE, CANCELLED_BY, CANCELLED_AT_TICKS, CANCEL_REASON)
                    VALUES (@TenantId, @OrganizationId, @Id, @Version, @Operation, @Document, @Amount, @Currency, @PaidAt,
                        @Method, @Reference, @Note, @CreatedBy, @State, @CancelledBy, @CancelledAt, @CancelReason)
                    """, values, ct);
                return;
            }
            // Only a recorded payment becomes cancelled; the payment input, operation and creator never change.
            var current = await FindPaymentAsync(value.Id, ct) ?? throw Failure("PAYMENT_NOT_FOUND");
            if (current.Version != expectedVersion) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (current.State != PaymentState.Recorded || value.State != PaymentState.Cancelled || current.Input != value.Input
                || current.OperationId != value.OperationId || current.CreatedBy != value.CreatedBy)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            await Write("UPDATE ERP_BILLING_PAYMENT SET VERSION=@Version, STATE=@State, CANCELLED_BY=@CancelledBy, CANCELLED_AT_TICKS=@CancelledAt, CANCEL_REASON=@CancelReason WHERE "
                + ScopeWhere + " AND PAYMENT_ID=@Id AND VERSION=@Previous AND STATE=0", values, ct);
        }
        public async Task<BusinessPage<PaymentRecord>> QueryPaymentsAsync(Guid documentId, PaymentState? state, int offset, int limit, CancellationToken ct)
        {
            const string filter = " AND DOCUMENT_ID=@Document AND (@State IS NULL OR STATE=@State)";
            var values = new { Document = Text(documentId), State = state.HasValue ? (int?)state.Value : null, Offset = offset, End = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_PAYMENT WHERE " + ScopeWhere + filter, values, ct);
            var rows = await Rows<PaymentRow>("SELECT * FROM (SELECT " + PaymentColumns + ", ROW_NUMBER() OVER (ORDER BY PAID_AT_TICKS, PAYMENT_ID) AS RowNumber "
                + "FROM ERP_BILLING_PAYMENT WHERE " + ScopeWhere + filter + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber", values, ct);
            return new(Array.AsReadOnly(rows.Select(Payment).ToArray()), total);
        }

        public Task AppendAuditAsync(BusinessAudit audit, CancellationToken ct)
        {
            if (audit.Actor != Actor) throw Failure("STORAGE_SCOPE_VIOLATION");
            return Write("""
                INSERT INTO ERP_BILLING_AUDIT (TENANT_ID, ORGANIZATION_ID, AUDIT_ID, USER_ID, RESOURCE_TYPE,
                    RESOURCE_ID, OPERATION, PREVIOUS_VERSION, VERSION, AT_TICKS)
                VALUES (@TenantId, @OrganizationId, @Id, @UserId, @ResourceType, @ResourceId, @Operation, @Previous, @Version, @At)
                """, new { Id = Text(audit.Id), audit.Actor.UserId, audit.ResourceType, ResourceId = Text(audit.ResourceId), audit.Operation,
                    Previous = Text(audit.PreviousVersion), Version = Text(audit.Version), At = audit.At.UtcTicks }, ct);
        }
        private void RequireScope(BusinessScope value) { if (value != Scope) throw Failure("STORAGE_SCOPE_VIOLATION"); }
    }

    private sealed class ContactRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string CustomerId { get; set; } = "";
    }
    private sealed class DocumentRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string OperationId { get; set; } = "";
        public int Kind { get; set; }
        public long Number { get; set; }
        public int Status { get; set; }
        public string ContactId { get; set; } = "";
        public string DocumentDate { get; set; } = "";
        public string DueDate { get; set; } = "";
        public string Currency { get; set; } = "";
        public int? DiscountType { get; set; }
        public string? DiscountValue { get; set; }
        public int? TaxType { get; set; }
        public string? TaxValue { get; set; }
        public int? Tax2Type { get; set; }
        public string? Tax2Value { get; set; }
        public string? Terms { get; set; }
        public string? Note { get; set; }
        public string Subtotal { get; set; } = "";
        public string DiscountAmount { get; set; } = "";
        public string TaxAmount { get; set; } = "";
        public string Total { get; set; } = "";
        public string Paid { get; set; } = "";
        public string CreatedBy { get; set; } = "";
        public string? ConvertedFrom { get; set; }
        public string? ConvertedTo { get; set; }
    }
    private sealed class LineRow
    {
        public string DocumentId { get; set; } = "";
        public int LineNumber { get; set; }
        public string Description { get; set; } = "";
        public string UnitPrice { get; set; } = "";
        public string Quantity { get; set; } = "";
        public bool ApplyTax { get; set; }
        public bool ApplyDiscount { get; set; }
    }
    private sealed class PaymentRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string OperationId { get; set; } = "";
        public string DocumentId { get; set; } = "";
        public string Amount { get; set; } = "";
        public string Currency { get; set; } = "";
        public long PaidAt { get; set; }
        public int Method { get; set; }
        public string? Reference { get; set; }
        public string? Note { get; set; }
        public string CreatedBy { get; set; } = "";
        public int State { get; set; }
        public string? CancelledBy { get; set; }
        public long? CancelledAt { get; set; }
        public string? CancelReason { get; set; }
    }
}
