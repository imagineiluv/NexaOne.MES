using System.Data;
using System.Data.Common;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    Task<BusinessPage<BusinessMembership>> IRecurringBridge.ListAccessibleScopesAsync(
        string userId, int offset, int limit, CancellationToken ct)
        => ListRecurringScopes(userId, offset, limit, ct);

    public Task<RecurringRule> CreateRuleAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, RecurringRuleInput input, CancellationToken ct = default)
        => RunRecurring(userId, tenantId, organizationId, "recurring.write",
            (service, session) => service.CreateRuleAsync(session.Actor, operationId, input, ct), ct);

    public Task<RecurringRule> GetRuleAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default)
        => RunRecurring(userId, tenantId, organizationId, "recurring.read",
            (service, session) => service.GetRuleAsync(session.Actor, id, ct), ct);

    public Task<BusinessPage<RecurringRule>> ListRulesAsync(string userId, Guid tenantId, Guid organizationId,
        RecurringRuleQuery? query = null, CancellationToken ct = default)
        => RunRecurring(userId, tenantId, organizationId, "recurring.read",
            (service, session) => service.ListRulesAsync(session.Actor, query, ct), ct);

    public Task<RecurringRule> DeactivateRuleAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => RunRecurring(userId, tenantId, organizationId, "recurring.write",
            (service, session) => service.DeactivateRuleAsync(session.Actor, id, version, ct), ct);

    public Task<RecurringExecution> ExecuteOccurrenceAsync(string userId, Guid tenantId, Guid organizationId,
        Guid ruleId, DateOnly month, CancellationToken ct = default)
        => RunRecurring(userId, tenantId, organizationId, "recurring.execute",
            (service, session) => service.ExecuteOccurrenceAsync(session.Actor, ruleId, month, ct), ct);

    private Task<T> RunRecurring<T>(string userId, Guid tenantId, Guid organizationId, string permission,
        Func<RecurringService, Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock);
            try
            {
                await session.Authorize(userId, permission, ct);
                var result = await action(new RecurringService(session, session, _clock), session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private Task<BusinessPage<BusinessMembership>> ListRecurringScopes(
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
                try
                {
                    batch = await _memberships.ListAccessInTransactionAsync(transaction, userId,
                        afterTenant, afterOrganization, batchSize, ct);
                }
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
                    if (!membership.Permissions.Any(grant => grant.StartsWith("recurring.", StringComparison.Ordinal))) continue;
                    if (total++ >= offset && items.Count < limit) items.Add(membership);
                }
                if (batch.Count < batchSize) break;
            }
            return new BusinessPage<BusinessMembership>(Array.AsReadOnly(items.ToArray()), total);
        }, IsolationLevel.Serializable, ct);
    }

    private sealed partial class Session
    {
        async Task<T> IAtomicBusinessStore<IRecurringTransaction>.ExecuteAsync<T>(BusinessScope requestedScope,
            Func<IRecurringTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            EnsureExecution(requestedScope, ct);
            var result = await work(this, ct); ct.ThrowIfCancellationRequested(); return result;
        }

        public Task<IncomeRecord?> FindIncomeAsync(Guid id, CancellationToken ct)
            => ReadIncome("INCOME_ID=@key", id, ct);
        public Task<IncomeRecord?> FindIncomeByOperationAsync(Guid operationId, CancellationToken ct)
            => ReadIncome("OPERATION_ID=@key", operationId, ct);
        private async Task<IncomeRecord?> ReadIncome(string predicate, Guid key, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_INCOME WHERE " + ScopeWhere
                + " AND " + predicate, new { key = Text(key) }, ct);
            return payload is null ? null : Deserialize<IncomeRecord>(payload);
        }
        public async Task SaveIncomeAsync(IncomeRecord value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            if (expectedVersion.HasValue)
            {
                var current = await FindIncomeAsync(value.Id, ct) ?? throw Failure("INCOME_NOT_FOUND");
                if (current.Version != expectedVersion) throw Failure("BUSINESS_VERSION_CONFLICT");
                if (current.OperationId != value.OperationId || current.CreatedBy != value.CreatedBy)
                    throw Failure("STORAGE_CONTRACT_VIOLATION");
            }
            var input = value.Input;
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId),
                Contact = Text(input.ContactId), Employee = Text(input.EmployeeId), ValueDate = Day(input.ValueDate),
                input.Currency, input.IsBonus, State = (int)value.State, Payload = Serialize(value),
                Previous = Text(expectedVersion)
            };
            await Write(expectedVersion is null ? """
                INSERT INTO ERP_INCOME (TENANT_ID,ORGANIZATION_ID,INCOME_ID,VERSION,OPERATION_ID,CONTACT_ID,
                    EMPLOYEE_ID,VALUE_DATE,CURRENCY,IS_BONUS,STATE,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@Contact,@Employee,@ValueDate,
                    @Currency,@IsBonus,@State,@Payload)
                """ : "UPDATE ERP_INCOME SET VERSION=@Version,CONTACT_ID=@Contact,EMPLOYEE_ID=@Employee,VALUE_DATE=@ValueDate,"
                    + "CURRENCY=@Currency,IS_BONUS=@IsBonus,STATE=@State,PAYLOAD=@Payload WHERE " + ScopeWhere
                    + " AND INCOME_ID=@Id AND VERSION=@Previous", values, ct);
        }
        public async Task<BusinessPage<IncomeRecord>> QueryIncomesAsync(IncomeQuery query, CancellationToken ct)
        {
            const string filter = " AND (@Start IS NULL OR VALUE_DATE>=@Start) AND (@EndDate IS NULL OR VALUE_DATE<=@EndDate)"
                + " AND (@Employee IS NULL OR EMPLOYEE_ID=@Employee) AND (@Contact IS NULL OR CONTACT_ID=@Contact)"
                + " AND (@IsBonus IS NULL OR IS_BONUS=@IsBonus) AND (@State IS NULL OR STATE=@State)";
            var values = new { Start = query.Start.HasValue ? Day(query.Start.Value) : null,
                EndDate = query.End.HasValue ? Day(query.End.Value) : null, Employee = Text(query.EmployeeId),
                Contact = Text(query.ContactId), query.IsBonus,
                State = query.State.HasValue ? (int?)query.State.Value : null,
                Offset = query.Offset, EndRow = (long)query.Offset + query.Limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_INCOME WHERE " + ScopeWhere + filter, values, ct);
            var rows = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM (SELECT PAYLOAD,ROW_NUMBER() OVER (ORDER BY VALUE_DATE,INCOME_ID) AS RowNumber "
                + "FROM ERP_INCOME WHERE " + ScopeWhere + filter + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber", values, ct);
            return new(Array.AsReadOnly(rows.Select(row => Deserialize<IncomeRecord>(row.Payload)).ToArray()), total);
        }

        public Task<RecurringRule?> FindRecurringRuleAsync(Guid id, CancellationToken ct)
            => ReadRecurringRule("RULE_ID=@key", Text(id), ct);
        public Task<RecurringRule?> FindRecurringRuleByOperationAsync(Guid operationId, CancellationToken ct)
            => ReadRecurringRule("OPERATION_ID=@key", Text(operationId), ct);
        private async Task<RecurringRule?> ReadRecurringRule(string predicate, string key, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_RECURRING_RULE WHERE " + ScopeWhere
                + " AND " + predicate, new { key }, ct);
            return payload is null ? null : Deserialize<RecurringRulePayload>(payload).ToRule();
        }
        public async Task SaveRecurringRuleAsync(RecurringRule value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            if (expectedVersion.HasValue)
            {
                var current = await FindRecurringRuleAsync(value.Id, ct) ?? throw Failure("RECURRING_RULE_NOT_FOUND");
                if (current.Version != expectedVersion) throw Failure("BUSINESS_VERSION_CONFLICT");
                if (current.OperationId != value.OperationId || !SameRecurringInput(current.Input, value.Input)
                    || current.Target != value.Target || current.CreatedBy != value.CreatedBy
                    || !current.Active || value.Active) throw Failure("STORAGE_CONTRACT_VIOLATION");
            }
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId),
                value.Input.Name, StartMonth = Day(value.Input.Schedule.StartMonth),
                EndMonth = value.Input.Schedule.EndMonth.HasValue ? Day(value.Input.Schedule.EndMonth.Value) : null,
                value.Input.Schedule.DayOfMonth, Target = (int)value.Target, value.Active,
                Payload = Serialize(RecurringRulePayload.From(value)), Previous = Text(expectedVersion)
            };
            await Write(expectedVersion is null ? """
                INSERT INTO ERP_RECURRING_RULE (TENANT_ID,ORGANIZATION_ID,RULE_ID,VERSION,OPERATION_ID,NAME,
                    START_MONTH,END_MONTH,DAY_OF_MONTH,TARGET,IS_ACTIVE,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@Name,@StartMonth,@EndMonth,
                    @DayOfMonth,@Target,@Active,@Payload)
                """ : "UPDATE ERP_RECURRING_RULE SET VERSION=@Version,IS_ACTIVE=@Active,PAYLOAD=@Payload WHERE "
                    + ScopeWhere + " AND RULE_ID=@Id AND VERSION=@Previous", values, ct);
        }
        public async Task<BusinessPage<RecurringRule>> QueryRecurringRulesAsync(RecurringRuleQuery query, CancellationToken ct)
        {
            const string filter = " AND (@Target IS NULL OR TARGET=@Target) AND (@Active IS NULL OR IS_ACTIVE=@Active)";
            var values = new { Target = query.Target.HasValue ? (int?)query.Target.Value : null, query.Active,
                Offset = query.Offset, EndRow = (long)query.Offset + query.Limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_RECURRING_RULE WHERE " + ScopeWhere + filter, values, ct);
            var rows = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM (SELECT PAYLOAD,ROW_NUMBER() OVER (ORDER BY NAME,RULE_ID) AS RowNumber "
                + "FROM ERP_RECURRING_RULE WHERE " + ScopeWhere + filter + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber", values, ct);
            return new(Array.AsReadOnly(rows.Select(row => Deserialize<RecurringRulePayload>(row.Payload).ToRule()).ToArray()), total);
        }

        private static bool SameRecurringInput(RecurringRuleInput left, RecurringRuleInput right)
        {
            if (left.Name != right.Name || left.Schedule != right.Schedule) return false;
            return (left.Template, right.Template) switch
            {
                (RecurringBillingTemplate a, RecurringBillingTemplate b) => a.Kind == b.Kind
                    && a.ContactId == b.ContactId && a.DueDays == b.DueDays && a.Currency == b.Currency
                    && a.Lines.SequenceEqual(b.Lines) && a.Discount == b.Discount && a.Tax == b.Tax
                    && a.Tax2 == b.Tax2 && a.Terms == b.Terms && a.Note == b.Note,
                (RecurringIncomeTemplate a, RecurringIncomeTemplate b) => a.Amount == b.Amount
                    && a.ContactId == b.ContactId && a.EmployeeId == b.EmployeeId && a.Currency == b.Currency
                    && a.IsBonus == b.IsBonus && a.Reference == b.Reference && a.Notes == b.Notes
                    && (a.TagIds ?? []).SequenceEqual(b.TagIds ?? []),
                (RecurringExpenseTemplate a, RecurringExpenseTemplate b) => a.Amount == b.Amount
                    && a.Type == b.Type && a.CategoryId == b.CategoryId && a.VendorId == b.VendorId
                    && a.EmployeeId == b.EmployeeId && a.ContactId == b.ContactId && a.ProjectId == b.ProjectId
                    && a.Currency == b.Currency && a.Purpose == b.Purpose && a.Reference == b.Reference
                    && a.Notes == b.Notes && a.Receipt == b.Receipt && a.Tax == b.Tax
                    && a.SplitAcrossEmployees == b.SplitAcrossEmployees
                    && (a.TagIds ?? []).SequenceEqual(b.TagIds ?? []),
                _ => false
            };
        }

        public async Task<RecurringOccurrence?> FindRecurringOccurrenceAsync(Guid ruleId, DateOnly month, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_RECURRING_OCCURRENCE WHERE " + ScopeWhere
                + " AND RULE_ID=@Rule AND OCCURRENCE_MONTH=@Month", new { Rule = Text(ruleId), Month = Day(month) }, ct);
            return payload is null ? null : Deserialize<RecurringOccurrence>(payload);
        }
        public Task SaveRecurringOccurrenceAsync(RecurringOccurrence value, CancellationToken ct)
        {
            RequireScope(value.Scope);
            return Write("""
                INSERT INTO ERP_RECURRING_OCCURRENCE (TENANT_ID,ORGANIZATION_ID,OCCURRENCE_ID,VERSION,RULE_ID,
                    OCCURRENCE_MONTH,TARGET,RESOURCE_ID,RESOURCE_OPERATION_ID,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Rule,@Month,@Target,@Resource,@ResourceOperation,@Payload)
                """, new { Id = Text(value.Id), Version = Text(value.Version), Rule = Text(value.RuleId),
                    Month = Day(value.Month), Target = (int)value.Target, Resource = Text(value.ResourceId),
                    ResourceOperation = Text(value.ResourceOperationId), Payload = Serialize(value) }, ct);
        }
    }

    private sealed record RecurringRulePayload(Guid Id, BusinessScope Scope, Guid Version, Guid OperationId,
        string Name, RecurringSchedule Schedule, RecurringTarget Target, string CreatedBy, bool Active,
        RecurringBillingTemplate? Billing, RecurringIncomeTemplate? Income, RecurringExpenseTemplate? Expense)
    {
        internal static RecurringRulePayload From(RecurringRule value) => value.Input.Template switch
        {
            RecurringBillingTemplate template => new(value.Id, value.Scope, value.Version, value.OperationId,
                value.Input.Name, value.Input.Schedule, value.Target, value.CreatedBy, value.Active, template, null, null),
            RecurringIncomeTemplate template => new(value.Id, value.Scope, value.Version, value.OperationId,
                value.Input.Name, value.Input.Schedule, value.Target, value.CreatedBy, value.Active, null, template, null),
            RecurringExpenseTemplate template => new(value.Id, value.Scope, value.Version, value.OperationId,
                value.Input.Name, value.Input.Schedule, value.Target, value.CreatedBy, value.Active, null, null, template),
            _ => throw Failure("STORAGE_CONTRACT_VIOLATION")
        };

        internal RecurringRule ToRule()
        {
            RecurringTemplate template = (Billing, Income, Expense) switch
            {
                ({ } value, null, null) => value,
                (null, { } value, null) => value,
                (null, null, { } value) => value,
                _ => throw new InvalidDataException("ERP recurring storage contains an invalid template.")
            };
            return new(Id, Scope, Version, OperationId, new(Name, Schedule, template), Target, CreatedBy, Active);
        }
    }
}
