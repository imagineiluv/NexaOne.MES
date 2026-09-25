using System.Data;
using System.Text.Json;
using Dapper;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    Task<BusinessPage<BusinessMembership>> IExpenseBridge.ListAccessibleScopesAsync(
        string userId, int offset, int limit, CancellationToken ct)
        => ListExpenseScopes(userId, offset, limit, ct);

    public Task<BusinessPage<ExpenseEmployee>> ListEmployeesAsync(string userId, Guid tenantId,
        Guid organizationId, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return RunExpense(userId, tenantId, organizationId, "expense.write",
            (_, _, session) => session.ListEmployeesAsync(offset, limit, ct), ct);
    }

    public Task<ExpenseCategory> CreateCategoryAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseCategoryInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (directory, _, session) => directory.CreateCategoryAsync(session.Actor, input, ct), ct);
    public Task<ExpenseCategory> GetCategoryAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.read",
            (directory, _, session) => directory.GetCategoryAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<ExpenseCategory>> ListCategoriesAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseDirectoryQuery? query = null, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.read",
            (directory, _, session) => directory.ListCategoriesAsync(session.Actor, query, ct), ct);
    public Task<ExpenseCategory> UpdateCategoryAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseCategoryInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (directory, _, session) => directory.UpdateCategoryAsync(session.Actor, id, version, input, ct), ct);
    public Task<ExpenseCategory> SetCategoryActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (directory, _, session) => directory.SetCategoryActiveAsync(session.Actor, id, version, active, ct), ct);

    public Task<ExpenseVendor> CreateVendorAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseVendorInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (directory, _, session) => directory.CreateVendorAsync(session.Actor, input, ct), ct);
    public Task<ExpenseVendor> GetVendorAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.read",
            (directory, _, session) => directory.GetVendorAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<ExpenseVendor>> ListVendorsAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseDirectoryQuery? query = null, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.read",
            (directory, _, session) => directory.ListVendorsAsync(session.Actor, query, ct), ct);
    public Task<ExpenseVendor> UpdateVendorAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseVendorInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (directory, _, session) => directory.UpdateVendorAsync(session.Actor, id, version, input, ct), ct);
    public Task<ExpenseVendor> SetVendorActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (directory, _, session) => directory.SetVendorActiveAsync(session.Actor, id, version, active, ct), ct);

    public Task<ExpenseRecord> CreateExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, ExpenseInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.write",
            (_, service, session) => service.CreateExpenseAsync(session.Actor, operationId, input, ct), ct);
    public Task<ExpenseRecord> UpdateExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.write",
            (_, service, session) => service.UpdateExpenseAsync(session.Actor, id, version, input, ct), ct);
    public Task<ExpenseRecord> GetExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.read",
            (_, service, session) => service.GetExpenseAsync(session.Actor, id, ct), ct);
    public Task<BusinessPage<ExpenseRecord>> ListExpensesAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseQuery? query = null, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.read",
            (_, service, session) => service.ListExpensesAsync(session.Actor, query, ct), ct);
    public Task<ExpenseRecord> MarkInvoicedAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.write",
            (_, service, session) => service.MarkInvoicedAsync(session.Actor, id, version, ct), ct);
    public Task<ExpenseRecord> MarkPaidAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.write",
            (_, service, session) => service.MarkPaidAsync(session.Actor, id, version, ct), ct);
    public Task<ExpenseRecord> ReimburseExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid id, Guid version, DateTimeOffset paidAt, string? reference = null,
        CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.reimburse",
            (_, service, session) => service.ReimburseExpenseAsync(session.Actor, operationId, id, version, paidAt, reference, ct), ct);
    public Task<ExpenseRecord> CancelExpenseAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, string? reason = null, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.write",
            (_, service, session) => service.CancelExpenseAsync(session.Actor, id, version, reason, ct), ct);

    public Task<ExpenseInvoiceLink> LinkInvoiceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid expenseId, Guid expenseVersion, Guid invoiceId, Guid invoiceVersion,
        string? description = null, CancellationToken ct = default)
        => RunAccounting(userId, tenantId, organizationId, (service, session) => service.LinkExpenseAsync(
            session.Actor, operationId, expenseId, expenseVersion, invoiceId, invoiceVersion, description, ct), ct);
    public Task<ExpenseInvoiceLink> UnlinkInvoiceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid expenseId, Guid expenseVersion, Guid invoiceId, Guid invoiceVersion,
        CancellationToken ct = default)
        => RunAccounting(userId, tenantId, organizationId, (service, session) => service.UnlinkExpenseAsync(
            session.Actor, operationId, expenseId, expenseVersion, invoiceId, invoiceVersion, ct), ct);

    private Task<T> RunExpense<T>(string userId, Guid tenantId, Guid organizationId, string permission,
        Func<ExpenseDirectoryService, ExpenseService, Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock,
                _projects);
            try
            {
                await session.Authorize(userId, permission, ct);
                var result = await action(new ExpenseDirectoryService(session, session),
                    new ExpenseService(session, session, _clock), session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private Task<T> RunAccounting<T>(string userId, Guid tenantId, Guid organizationId,
        Func<ExpenseAccountingService, Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock,
                _projects);
            try
            {
                await session.Authorize(userId, "expense.invoice", ct);
                var result = await action(new ExpenseAccountingService(session, session), session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private Task<BusinessPage<BusinessMembership>> ListExpenseScopes(
        string userId, int offset, int limit, CancellationToken ct)
    {
        if (!ValidText(userId, 50)) throw Failure("BUSINESS_ACCESS_DENIED");
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return _processor.ExecuteInTransactionAsync(async (_, transaction) =>
        {
            const int batchSize = 128;
            var items = new List<BusinessMembership>(limit); long total = 0;
            Guid? afterTenant = null, afterOrganization = null;
            while (true)
            {
                IReadOnlyList<BusinessMembership> batch;
                try { batch = await _memberships.ListAccessInTransactionAsync(
                    transaction, userId, afterTenant, afterOrganization, batchSize, ct); }
                catch (InvalidDataException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
                if (batch is null || batch.Count > batchSize) throw Failure("STORAGE_CONTRACT_VIOLATION");
                foreach (var membership in batch)
                {
                    if (membership is null || membership.TenantId == Guid.Empty || membership.OrganizationId == Guid.Empty
                        || membership.BusinessUserId == Guid.Empty || !membership.IsActive || membership.Version <= 0
                        || membership.Permissions is null) throw Failure("STORAGE_CONTRACT_VIOLATION");
                    if (afterTenant.HasValue)
                    {
                        var tenantOrder = string.CompareOrdinal(Text(membership.TenantId), Text(afterTenant.Value));
                        if (tenantOrder < 0 || tenantOrder == 0
                            && string.CompareOrdinal(Text(membership.OrganizationId), Text(afterOrganization!.Value)) <= 0)
                            throw Failure("STORAGE_CONTRACT_VIOLATION");
                    }
                    afterTenant = membership.TenantId; afterOrganization = membership.OrganizationId;
                    if (!membership.Permissions.Any(grant => grant.StartsWith("expense.", StringComparison.Ordinal))) continue;
                    if (total++ >= offset && items.Count < limit) items.Add(membership);
                }
                if (batch.Count < batchSize) break;
            }
            return new BusinessPage<BusinessMembership>(Array.AsReadOnly(items.ToArray()), total);
        }, IsolationLevel.Serializable, ct);
    }

    private sealed partial class Session
    {
        private static readonly JsonSerializerOptions Json = new();

        async Task<T> IAtomicBusinessStore<IExpenseTransaction>.ExecuteAsync<T>(BusinessScope requestedScope,
            Func<IExpenseTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            EnsureExecution(requestedScope, ct);
            var result = await work(this, ct); ct.ThrowIfCancellationRequested(); return result;
        }
        async Task<T> IAtomicBusinessStore<IExpenseAccountingTransaction>.ExecuteAsync<T>(BusinessScope requestedScope,
            Func<IExpenseAccountingTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            EnsureExecution(requestedScope, ct);
            var result = await work(this, ct); ct.ThrowIfCancellationRequested(); return result;
        }
        private void EnsureExecution(BusinessScope requestedScope, CancellationToken ct)
        {
            if (!_open || requestedScope != Scope) throw Failure("STORAGE_SCOPE_VIOLATION");
            if (Interlocked.Exchange(ref _invoked, 1) != 0) throw Failure("TRANSACTION_REPLAY_NOT_ALLOWED");
            ct.ThrowIfCancellationRequested();
        }

        private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
        private static T Deserialize<T>(string payload)
        {
            try { return JsonSerializer.Deserialize<T>(payload, Json)
                ?? throw new InvalidDataException("ERP expense storage contains a null payload."); }
            catch (JsonException error) { throw new InvalidDataException("ERP expense storage contains invalid JSON.", error); }
        }
        private static string NameKey(string value) => value.ToUpperInvariant();

        public Task<ExpenseCategory?> FindExpenseCategoryAsync(Guid id, CancellationToken ct)
            => ReadCategory("CATEGORY_ID=@key", Text(id), ct);
        public Task<ExpenseCategory?> FindExpenseCategoryByNameAsync(string name, CancellationToken ct)
            => ReadCategory("NAME_KEY=@key", NameKey(name), ct);
        private async Task<ExpenseCategory?> ReadCategory(string predicate, string key, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_EXPENSE_CATEGORY WHERE "
                + ScopeWhere + " AND " + predicate, new { key }, ct);
            return payload is null ? null : Deserialize<ExpenseCategory>(payload);
        }
        public async Task SaveExpenseCategoryAsync(ExpenseCategory value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            var values = new { Id = Text(value.Id), Version = Text(value.Version), value.Input.Name,
                NameKey = NameKey(value.Input.Name), Active = value.Active, Payload = Serialize(value), Previous = Text(expectedVersion) };
            await Write(expectedVersion is null
                ? "INSERT INTO ERP_EXPENSE_CATEGORY (TENANT_ID,ORGANIZATION_ID,CATEGORY_ID,VERSION,NAME,NAME_KEY,IS_ACTIVE,PAYLOAD) VALUES (@TenantId,@OrganizationId,@Id,@Version,@Name,@NameKey,@Active,@Payload)"
                : "UPDATE ERP_EXPENSE_CATEGORY SET VERSION=@Version,NAME=@Name,NAME_KEY=@NameKey,IS_ACTIVE=@Active,PAYLOAD=@Payload WHERE "
                    + ScopeWhere + " AND CATEGORY_ID=@Id AND VERSION=@Previous", values, ct);
        }
        public Task<BusinessPage<ExpenseCategory>> QueryExpenseCategoriesAsync(ExpenseDirectoryQuery query, CancellationToken ct)
            => QueryDirectory<ExpenseCategory>("ERP_EXPENSE_CATEGORY", "CATEGORY_ID", query, ct);

        public Task<ExpenseVendor?> FindExpenseVendorAsync(Guid id, CancellationToken ct)
            => ReadVendor("VENDOR_ID=@key", Text(id), ct);
        public Task<ExpenseVendor?> FindExpenseVendorByNameAsync(string name, CancellationToken ct)
            => ReadVendor("NAME_KEY=@key", NameKey(name), ct);
        private async Task<ExpenseVendor?> ReadVendor(string predicate, string key, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_EXPENSE_VENDOR WHERE "
                + ScopeWhere + " AND " + predicate, new { key }, ct);
            return payload is null ? null : Deserialize<ExpenseVendor>(payload);
        }
        public async Task SaveExpenseVendorAsync(ExpenseVendor value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            var values = new { Id = Text(value.Id), Version = Text(value.Version), value.Input.Name,
                NameKey = NameKey(value.Input.Name), Active = value.Active, Payload = Serialize(value), Previous = Text(expectedVersion) };
            await Write(expectedVersion is null
                ? "INSERT INTO ERP_EXPENSE_VENDOR (TENANT_ID,ORGANIZATION_ID,VENDOR_ID,VERSION,NAME,NAME_KEY,IS_ACTIVE,PAYLOAD) VALUES (@TenantId,@OrganizationId,@Id,@Version,@Name,@NameKey,@Active,@Payload)"
                : "UPDATE ERP_EXPENSE_VENDOR SET VERSION=@Version,NAME=@Name,NAME_KEY=@NameKey,IS_ACTIVE=@Active,PAYLOAD=@Payload WHERE "
                    + ScopeWhere + " AND VENDOR_ID=@Id AND VERSION=@Previous", values, ct);
        }
        public Task<BusinessPage<ExpenseVendor>> QueryExpenseVendorsAsync(ExpenseDirectoryQuery query, CancellationToken ct)
            => QueryDirectory<ExpenseVendor>("ERP_EXPENSE_VENDOR", "VENDOR_ID", query, ct);

        private async Task<BusinessPage<T>> QueryDirectory<T>(string table, string idColumn,
            ExpenseDirectoryQuery query, CancellationToken ct)
        {
            const string filter = " AND (@IncludeInactive=1 OR IS_ACTIVE=1)"
                + " AND (@Pattern IS NULL OR NAME_KEY LIKE @Pattern ESCAPE '~')";
            var values = new
            {
                query.IncludeInactive,
                Pattern = query.Text is null ? null : "%" + EscapeLike(NameKey(query.Text)) + "%",
                Offset = query.Offset,
                EndRow = (long)query.Offset + query.Limit
            };
            var total = await Scalar<long>("SELECT COUNT(*) FROM " + table + " WHERE "
                + ScopeWhere + filter, values, ct);
            var payloads = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM (SELECT PAYLOAD,"
                + "ROW_NUMBER() OVER (ORDER BY NAME," + idColumn + ") AS RowNumber FROM " + table
                + " WHERE " + ScopeWhere + filter
                + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber", values, ct);
            return new(Array.AsReadOnly(payloads.Select(row => Deserialize<T>(row.Payload)).ToArray()), total);
        }

        private static string EscapeLike(string value) => value
            .Replace("~", "~~", StringComparison.Ordinal)
            .Replace("%", "~%", StringComparison.Ordinal)
            .Replace("_", "~_", StringComparison.Ordinal)
            .Replace("[", "~[", StringComparison.Ordinal);

        public async Task<bool> EmployeeExistsAsync(Guid employeeId, CancellationToken ct)
            => await memberships.GetActiveMemberInTransactionAsync(transaction, Id(Scope.TenantId),
                Id(Scope.OrganizationId), employeeId, ct) is not null;
        internal async Task<BusinessPage<ExpenseEmployee>> ListEmployeesAsync(
            int offset, int limit, CancellationToken ct)
        {
            const int batchSize = 128;
            var tenantId = Id(Scope.TenantId); var organizationId = Id(Scope.OrganizationId);
            var items = new List<ExpenseEmployee>(limit); long total = 0; Guid? cursor = null;
            while (true)
            {
                var batch = await memberships.ListActiveMembersInTransactionAsync(transaction,
                    tenantId, organizationId, cursor, batchSize, ct);
                if (batch is null || batch.Count > batchSize) throw Failure("STORAGE_CONTRACT_VIOLATION");
                foreach (var member in batch)
                {
                    if (!member.IsActive || member.TenantId != tenantId || member.OrganizationId != organizationId
                        || member.BusinessUserId == Guid.Empty || member.Version <= 0
                        || !ValidText(member.UserId, 50)
                        || cursor.HasValue && string.CompareOrdinal(Text(member.BusinessUserId), Text(cursor.Value)) <= 0)
                        throw Failure("STORAGE_CONTRACT_VIOLATION");
                    cursor = member.BusinessUserId;
                    if (total++ >= offset && items.Count < limit)
                        items.Add(new(member.BusinessUserId, member.UserId));
                }
                if (batch.Count < batchSize) break;
            }
            return new(Array.AsReadOnly(items.ToArray()), total);
        }
        public async Task<IReadOnlyList<Guid>> ListActiveEmployeeIdsAsync(CancellationToken ct)
        {
            const int batchSize = 128;
            var tenantId = Id(Scope.TenantId); var organizationId = Id(Scope.OrganizationId);
            var ids = new List<Guid>(); Guid? cursor = null;
            while (true)
            {
                var batch = await memberships.ListActiveMembersInTransactionAsync(transaction,
                    tenantId, organizationId, cursor, batchSize, ct);
                if (batch is null || batch.Count > batchSize) throw Failure("STORAGE_CONTRACT_VIOLATION");
                foreach (var member in batch)
                {
                    if (!member.IsActive || member.TenantId != tenantId || member.OrganizationId != organizationId
                        || member.BusinessUserId == Guid.Empty || member.Version <= 0
                        || cursor.HasValue && string.CompareOrdinal(Text(member.BusinessUserId), Text(cursor.Value)) <= 0)
                        throw Failure("STORAGE_CONTRACT_VIOLATION");
                    ids.Add(member.BusinessUserId); cursor = member.BusinessUserId;
                }
                if (batch.Count < batchSize) break;
            }
            return Array.AsReadOnly(ids.ToArray());
        }
        public async Task<bool> ProjectExistsAsync(Guid projectId, CancellationToken ct)
            => projects is not null && await projects.ProjectExistsInTransactionAsync(transaction,
                Id(Scope.TenantId), Id(Scope.OrganizationId), projectId, ct);
        // MES still has no organization-scoped business-tag master. FDC equipment tags and POM work
        // scopes are different domains, so nonempty values remain fail-closed until that owner exists.
        public Task<bool> TagsExistAsync(IReadOnlyList<Guid> tagIds, CancellationToken ct)
            => Task.FromResult(tagIds.Count == 0);

        public Task<ExpenseRecord?> FindExpenseAsync(Guid id, CancellationToken ct)
            => ReadExpense("EXPENSE_ID=@key", id, ct);
        public Task<ExpenseRecord?> FindExpenseByOperationAsync(Guid operationId, CancellationToken ct)
            => ReadExpense("OPERATION_ID=@key", operationId, ct);
        public Task<ExpenseRecord?> FindExpenseByReimbursementOperationAsync(Guid operationId, CancellationToken ct)
            => ReadExpense("REIMBURSEMENT_OPERATION_ID=@key", operationId, ct);
        private async Task<ExpenseRecord?> ReadExpense(string predicate, Guid key, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_EXPENSE WHERE " + ScopeWhere
                + " AND " + predicate, new { key = Text(key) }, ct);
            return payload is null ? null : Deserialize<ExpenseRecord>(payload);
        }
        public async Task SaveExpenseAsync(ExpenseRecord value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            if (expectedVersion.HasValue)
            {
                var current = await FindExpenseAsync(value.Id, ct) ?? throw Failure("EXPENSE_NOT_FOUND");
                if (current.Version != expectedVersion) throw Failure("BUSINESS_VERSION_CONFLICT");
                if (current.OperationId != value.OperationId || current.CreatedBy != value.CreatedBy
                    || !SameInput(current.CreationInput, value.CreationInput))
                    throw Failure("STORAGE_CONTRACT_VIOLATION");
            }
            var input = value.Input;
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId),
                ReimbursementOperation = Text(value.Reimbursement?.OperationId), Category = Text(input.CategoryId),
                Vendor = Text(input.VendorId), Employee = Text(input.EmployeeId), Contact = Text(input.ContactId),
                Project = Text(input.ProjectId), Invoice = Text(value.InvoiceId), ValueDate = Day(input.ValueDate),
                input.Currency, Type = (int)input.Type, Status = (int)value.Status, State = (int)value.State,
                Payload = Serialize(value), Previous = Text(expectedVersion)
            };
            await Write(expectedVersion is null ? """
                INSERT INTO ERP_EXPENSE (TENANT_ID,ORGANIZATION_ID,EXPENSE_ID,VERSION,OPERATION_ID,REIMBURSEMENT_OPERATION_ID,
                    CATEGORY_ID,VENDOR_ID,EMPLOYEE_ID,CONTACT_ID,PROJECT_ID,INVOICE_ID,VALUE_DATE,CURRENCY,TYPE,STATUS,STATE,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@ReimbursementOperation,@Category,@Vendor,@Employee,
                    @Contact,@Project,@Invoice,@ValueDate,@Currency,@Type,@Status,@State,@Payload)
                """ : "UPDATE ERP_EXPENSE SET VERSION=@Version,REIMBURSEMENT_OPERATION_ID=@ReimbursementOperation,CATEGORY_ID=@Category,"
                    + "VENDOR_ID=@Vendor,EMPLOYEE_ID=@Employee,CONTACT_ID=@Contact,PROJECT_ID=@Project,INVOICE_ID=@Invoice,VALUE_DATE=@ValueDate,"
                    + "CURRENCY=@Currency,TYPE=@Type,STATUS=@Status,STATE=@State,PAYLOAD=@Payload WHERE " + ScopeWhere
                    + " AND EXPENSE_ID=@Id AND VERSION=@Previous", values, ct);
        }

        private static bool SameInput(ExpenseInput left, ExpenseInput right)
            => left.Amount == right.Amount && left.Type == right.Type && left.CategoryId == right.CategoryId
                && left.VendorId == right.VendorId && left.EmployeeId == right.EmployeeId
                && left.ContactId == right.ContactId && left.ProjectId == right.ProjectId
                && left.Currency == right.Currency && left.ValueDate == right.ValueDate
                && left.Purpose == right.Purpose && left.Reference == right.Reference && left.Notes == right.Notes
                && left.Receipt == right.Receipt && left.Tax == right.Tax
                && left.SplitAcrossEmployees == right.SplitAcrossEmployees
                && (left.TagIds ?? []).SequenceEqual(right.TagIds ?? []);
        public async Task<BusinessPage<ExpenseRecord>> QueryExpensesAsync(ExpenseQuery query, CancellationToken ct)
        {
            const string filter = " AND (@Start IS NULL OR VALUE_DATE>=@Start) AND (@EndDate IS NULL OR VALUE_DATE<=@EndDate)"
                + " AND (@Employee IS NULL OR EMPLOYEE_ID=@Employee) AND (@Contact IS NULL OR CONTACT_ID=@Contact)"
                + " AND (@Project IS NULL OR PROJECT_ID=@Project) AND (@Category IS NULL OR CATEGORY_ID=@Category)"
                + " AND (@Vendor IS NULL OR VENDOR_ID=@Vendor) AND (@Type IS NULL OR TYPE=@Type)"
                + " AND (@Status IS NULL OR STATUS=@Status) AND (@State IS NULL OR STATE=@State)";
            var values = new { Start = query.Start.HasValue ? Day(query.Start.Value) : null,
                EndDate = query.End.HasValue ? Day(query.End.Value) : null, Employee = Text(query.EmployeeId),
                Contact = Text(query.ContactId), Project = Text(query.ProjectId), Category = Text(query.CategoryId),
                Vendor = Text(query.VendorId), Type = query.Type.HasValue ? (int?)query.Type.Value : null,
                Status = query.Status.HasValue ? (int?)query.Status.Value : null,
                State = query.State.HasValue ? (int?)query.State.Value : null,
                Offset = query.Offset, EndRow = (long)query.Offset + query.Limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_EXPENSE WHERE " + ScopeWhere + filter, values, ct);
            var rows = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM (SELECT PAYLOAD,ROW_NUMBER() OVER (ORDER BY VALUE_DATE,EXPENSE_ID) AS RowNumber "
                + "FROM ERP_EXPENSE WHERE " + ScopeWhere + filter + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber", values, ct);
            return new(Array.AsReadOnly(rows.Select(row => Deserialize<ExpenseRecord>(row.Payload)).ToArray()), total);
        }

        private sealed class PayloadRow { public string Payload { get; set; } = ""; }
    }
}
