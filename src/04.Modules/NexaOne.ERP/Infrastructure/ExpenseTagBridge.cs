using NexaFramework.Service;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    public Task<ExpenseTag> CreateTagAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseTagInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (_, _, session) => session.CreateTagAsync(input, ct), ct);

    public Task<ExpenseTag> GetTagAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.read",
            (_, _, session) => session.GetTagAsync(id, ct), ct);

    public Task<BusinessPage<ExpenseTag>> ListTagsAsync(string userId, Guid tenantId, Guid organizationId,
        ExpenseTagQuery? query = null, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.read",
            (_, _, session) => session.ListTagsAsync(query ?? new(), ct), ct);

    public Task<ExpenseTag> UpdateTagAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, ExpenseTagInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (_, _, session) => session.UpdateTagAsync(id, version, input, ct), ct);

    public Task<ExpenseTag> SetTagActiveAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, bool active, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.directory.write",
            (_, _, session) => session.SetTagActiveAsync(id, version, active, ct), ct);

    private static ExpenseTagInput ValidateTagInput(ExpenseTagInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var name = input.Name?.Trim();
        RequireText(name, 255);
        return new(name!);
    }

    private static ExpenseTagQuery ValidateTagQuery(ExpenseTagQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Offset < 0 || query.Limit is < 1 or > 100)
            throw Failure("INVALID_BUSINESS_INPUT");
        var text = string.IsNullOrWhiteSpace(query.Text) ? null : query.Text.Trim();
        if (text is not null && !ValidText(text, 255)) throw Failure("INVALID_BUSINESS_INPUT");
        return query with { Text = text };
    }

    private sealed partial class Session
    {
        internal async Task<ExpenseTag> CreateTagAsync(ExpenseTagInput input, CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            var valid = ValidateTagInput(input);
            if (await ReadTagAsync("NAME_KEY=@key", NameKey(valid.Name), ct) is not null)
                throw Failure("EXPENSE_TAG_NAME_EXISTS");
            var next = new ExpenseTag(Guid.NewGuid(), Scope, Guid.NewGuid(), valid);
            await SaveTagAsync(next, null, ct);
            await AuditTagAsync(next, "created", null, ct);
            return next;
        }

        internal async Task<ExpenseTag> GetTagAsync(Guid id, CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            if (id == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            return await RequireTagAsync(id, ct);
        }

        internal async Task<BusinessPage<ExpenseTag>> ListTagsAsync(ExpenseTagQuery query,
            CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            var valid = ValidateTagQuery(query);
            const string filter = " AND (@IncludeInactive=1 OR IS_ACTIVE=1)"
                + " AND (@Pattern IS NULL OR NAME_KEY LIKE @Pattern ESCAPE '~')";
            var values = new
            {
                valid.IncludeInactive,
                Pattern = valid.Text is null ? null : "%" + EscapeLike(NameKey(valid.Text)) + "%",
                valid.Offset,
                EndRow = (long)valid.Offset + valid.Limit
            };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_EXPENSE_TAG WHERE "
                + ScopeWhere + filter, values, ct);
            var rows = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM (SELECT PAYLOAD,"
                + "ROW_NUMBER() OVER (ORDER BY NAME,TAG_ID) AS RowNumber FROM ERP_EXPENSE_TAG WHERE "
                + ScopeWhere + filter
                + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber", values, ct);
            var items = rows.Select(row => Deserialize<ExpenseTag>(row.Payload)).ToArray();
            if (items.Length > valid.Limit || total < items.Length || items.Select(item => item.Id).Distinct().Count() != items.Length)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            foreach (var item in items)
            {
                ValidateStoredTag(item);
                if (!valid.IncludeInactive && !item.Active
                    || valid.Text is not null && !item.Input.Name.Contains(valid.Text, StringComparison.OrdinalIgnoreCase))
                    throw Failure("STORAGE_CONTRACT_VIOLATION");
            }
            return new(Array.AsReadOnly(items), total);
        }

        internal async Task<ExpenseTag> UpdateTagAsync(Guid id, Guid version, ExpenseTagInput input,
            CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            if (id == Guid.Empty || version == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var valid = ValidateTagInput(input);
            var current = await RequireTagAsync(id, ct);
            if (current.Version != version) throw Failure("BUSINESS_VERSION_CONFLICT");
            var duplicate = await ReadTagAsync("NAME_KEY=@key", NameKey(valid.Name), ct);
            if (duplicate is not null && duplicate.Id != id) throw Failure("EXPENSE_TAG_NAME_EXISTS");
            if (current.Input == valid) return current;
            var next = current with { Version = Guid.NewGuid(), Input = valid };
            await SaveTagAsync(next, version, ct);
            await AuditTagAsync(next, "updated", version, ct);
            return next;
        }

        internal async Task<ExpenseTag> SetTagActiveAsync(Guid id, Guid version, bool active,
            CancellationToken ct)
        {
            EnsureExecution(Scope, ct);
            if (id == Guid.Empty || version == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var current = await RequireTagAsync(id, ct);
            if (current.Version != version) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (current.Active == active) return current;
            var next = current with { Version = Guid.NewGuid(), Active = active };
            await SaveTagAsync(next, version, ct);
            await AuditTagAsync(next, active ? "activated" : "deactivated", version, ct);
            return next;
        }

        private async Task<ExpenseTag> RequireTagAsync(Guid id, CancellationToken ct)
            => await ReadTagAsync("TAG_ID=@key", Text(id), ct)
                ?? throw Failure("EXPENSE_TAG_NOT_FOUND");

        private async Task<ExpenseTag?> ReadTagAsync(string predicate, string key, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_EXPENSE_TAG WHERE "
                + ScopeWhere + " AND " + predicate, new { key }, ct);
            if (payload is null) return null;
            var value = Deserialize<ExpenseTag>(payload);
            ValidateStoredTag(value);
            return value;
        }

        private void ValidateStoredTag(ExpenseTag value)
        {
            if (value.Id == Guid.Empty || value.Version == Guid.Empty || value.Scope != Scope
                || value.Input is null || !ValidText(value.Input.Name, 255))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        private Task SaveTagAsync(ExpenseTag value, Guid? expectedVersion, CancellationToken ct)
        {
            ValidateStoredTag(value);
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), value.Input.Name,
                NameKey = NameKey(value.Input.Name), Active = value.Active,
                Payload = Serialize(value), Previous = Text(expectedVersion)
            };
            return Write(expectedVersion is null
                ? "INSERT INTO ERP_EXPENSE_TAG (TENANT_ID,ORGANIZATION_ID,TAG_ID,VERSION,NAME,NAME_KEY,IS_ACTIVE,PAYLOAD) VALUES (@TenantId,@OrganizationId,@Id,@Version,@Name,@NameKey,@Active,@Payload)"
                : "UPDATE ERP_EXPENSE_TAG SET VERSION=@Version,NAME=@Name,NAME_KEY=@NameKey,IS_ACTIVE=@Active,PAYLOAD=@Payload WHERE "
                    + ScopeWhere + " AND TAG_ID=@Id AND VERSION=@Previous", values, ct);
        }

        private Task AuditTagAsync(ExpenseTag value, string operation, Guid? previous,
            CancellationToken ct)
            => AppendAuditAsync(new(Guid.NewGuid(), Actor, "expense-tag", value.Id, operation,
                previous, value.Version, clock.GetUtcNow()), ct);

        internal async Task<bool> TagsExistCoreAsync(IReadOnlyList<Guid> tagIds, CancellationToken ct)
        {
            if (tagIds.Count == 0) return true;
            if (tagIds.Any(id => id == Guid.Empty) || tagIds.Distinct().Count() != tagIds.Count) return false;
            var count = await Scalar<long>("SELECT COUNT(*) FROM ERP_EXPENSE_TAG WHERE " + ScopeWhere
                + " AND IS_ACTIVE=1 AND TAG_ID IN @TagIds",
                new { TagIds = tagIds.Select(Text).ToArray() }, ct);
            return count == tagIds.Count;
        }
    }
}
