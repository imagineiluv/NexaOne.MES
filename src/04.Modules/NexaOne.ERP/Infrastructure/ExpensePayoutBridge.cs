using System.Data;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    public Task<ExpensePayoutRequest> QueuePayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, ExpensePayoutInput input, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.reimburse", async (_, service, session) =>
        {
            if (operationId == Guid.Empty || input is null || input.ExpenseId == Guid.Empty
                || input.ExpenseVersion == Guid.Empty || !ValidText(input.ProviderKey, 100))
                throw Failure("INVALID_BUSINESS_INPUT");
            if (await session.FindPayoutByOperationAsync(operationId, ct) is { } replay)
            {
                if (replay.ExpenseId != input.ExpenseId || replay.ExpenseVersion != input.ExpenseVersion
                    || replay.ProviderKey != input.ProviderKey)
                    throw Failure("EXPENSE_PAYOUT_OPERATION_CONFLICT");
                return replay;
            }
            var expense = await service.GetExpenseAsync(session.Actor, input.ExpenseId, ct);
            if (expense.Version != input.ExpenseVersion) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (expense.State != ExpenseState.Active || expense.Input.EmployeeId is not { } employeeId
                || expense.Allocations.Count != 0 || expense.Reimbursement is not null)
                throw Failure("EXPENSE_NOT_REIMBURSABLE");
            var expected = expense.Input.Type == ExpenseType.BillableToContact
                ? ExpenseStatus.Invoiced : ExpenseStatus.NotBillable;
            if (expense.Status != expected) throw Failure("EXPENSE_INVALID_STATUS");
            if (await session.FindActivePayoutByExpenseAsync(expense.Id, ct) is not null)
                throw Failure("EXPENSE_PAYOUT_ALREADY_ACTIVE");
            var now = _clock.GetUtcNow();
            var payout = new ExpensePayoutRequest(Guid.NewGuid(), Guid.NewGuid(), operationId, expense.Scope,
                expense.Id, expense.Version, employeeId, expense.Amounts.Gross, expense.Input.Currency,
                input.ProviderKey, ExpensePayoutState.Pending, 0, now, session.Actor.UserId);
            await session.SavePayoutAsync(payout, null, ct);
            await session.AppendAuditAsync(new(Guid.NewGuid(), session.Actor, "expense-payout", payout.Id,
                "queued", null, payout.Version, now), ct);
            return payout;
        }, ct);

    public Task<ExpensePayoutRequest> GetPayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.read", async (_, _, session) =>
            await session.FindPayoutAsync(payoutId, ct) ?? throw Failure("EXPENSE_PAYOUT_NOT_FOUND"), ct);

    public Task<BusinessPage<ExpensePayoutRequest>> ListPayoutsAsync(string userId, Guid tenantId,
        Guid organizationId, Guid? expenseId = null, int offset = 0, int limit = 50,
        CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.read", (_, _, session) =>
        {
            if (expenseId == Guid.Empty || offset < 0 || limit is < 1 or > 100)
                throw Failure("INVALID_BUSINESS_INPUT");
            return session.QueryPayoutsAsync(expenseId, offset, limit, ct);
        }, ct);

    public Task<ExpensePayoutRequest> CancelPayoutAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, Guid version, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.reimburse", async (_, _, session) =>
        {
            var current = await session.FindPayoutAsync(payoutId, ct)
                ?? throw Failure("EXPENSE_PAYOUT_NOT_FOUND");
            if (current.State == ExpensePayoutState.Cancelled) return current;
            if (current.Version != version) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (current.State != ExpensePayoutState.Pending) throw Failure("EXPENSE_PAYOUT_LOCKED");
            var next = current with { Version = Guid.NewGuid(), State = ExpensePayoutState.Cancelled };
            await session.SavePayoutAsync(next, version, ct);
            await session.AppendAuditAsync(new(Guid.NewGuid(), session.Actor, "expense-payout", next.Id,
                "cancelled", version, next.Version, _clock.GetUtcNow()), ct);
            return next;
        }, ct);

    async Task<BusinessPage<BusinessMembership>> IExpensePayoutAutomationBridge.ListScopesAsync(
        string userId, int offset, int limit, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        var matches = new List<BusinessMembership>();
        var sourceOffset = 0;
        while (true)
        {
            var page = await ListExpenseScopes(userId, sourceOffset, 100, ct);
            matches.AddRange(page.Items.Where(x => x.Permissions.Contains("expense.reimburse", StringComparer.Ordinal)));
            sourceOffset += page.Items.Count;
            if (sourceOffset >= page.Total || page.Items.Count == 0) break;
        }
        return new(Array.AsReadOnly(matches.Skip(offset).Take(limit).ToArray()), matches.Count);
    }

    public Task<IReadOnlyList<ExpensePayoutRequest>> ClaimDueAsync(string userId, Guid tenantId,
        Guid organizationId, int limit, TimeSpan leaseDuration, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.reimburse", async (_, _, session) =>
        {
            if (limit is < 1 or > 100 || leaseDuration < TimeSpan.FromSeconds(10)
                || leaseDuration > TimeSpan.FromMinutes(15)) throw Failure("INVALID_BUSINESS_INPUT");
            var now = _clock.GetUtcNow();
            var due = await session.QueryDuePayoutsAsync(now, limit, ct);
            var claimed = new List<ExpensePayoutRequest>(due.Count);
            foreach (var current in due)
            {
                var next = current with
                {
                    Version = Guid.NewGuid(), State = ExpensePayoutState.Processing,
                    AttemptCount = checked(current.AttemptCount + 1), LeaseId = Guid.NewGuid(),
                    LeaseExpiresAt = now + leaseDuration, ErrorCode = null
                };
                await session.SavePayoutAsync(next, current.Version, ct);
                await session.AppendAuditAsync(new(Guid.NewGuid(), session.Actor, "expense-payout", next.Id,
                    "leased", current.Version, next.Version, now), ct);
                claimed.Add(next);
            }
            return (IReadOnlyList<ExpensePayoutRequest>)Array.AsReadOnly(claimed.ToArray());
        }, ct);

    public Task<ExpensePayoutRequest> CompleteAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, Guid version, Guid leaseId, ExpensePayoutReceipt receipt,
        CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.reimburse", async (_, service, session) =>
        {
            if (receipt is null || !ValidText(receipt.Reference, 255) || receipt.PaidAt == default
                || receipt.PaidAt.Offset != TimeSpan.Zero) throw Failure("INVALID_BUSINESS_INPUT");
            var current = await session.RequireLeasedPayoutAsync(payoutId, version, leaseId, _clock.GetUtcNow(), ct);
            await service.ReimburseExpenseAsync(session.Actor, current.OperationId, current.ExpenseId,
                current.ExpenseVersion, receipt.PaidAt, receipt.Reference, ct);
            var next = current with
            {
                Version = Guid.NewGuid(), State = ExpensePayoutState.Completed,
                LeaseId = null, LeaseExpiresAt = null, ProviderReference = receipt.Reference,
                PaidAt = receipt.PaidAt, ErrorCode = null
            };
            await session.SavePayoutAsync(next, version, ct);
            await session.AppendAuditAsync(new(Guid.NewGuid(), session.Actor, "expense-payout", next.Id,
                "completed", version, next.Version, _clock.GetUtcNow()), ct);
            return next;
        }, ct);

    public Task<ExpensePayoutRequest> FailAsync(string userId, Guid tenantId, Guid organizationId,
        Guid payoutId, Guid version, Guid leaseId, string errorCode, CancellationToken ct = default)
        => RunExpense(userId, tenantId, organizationId, "expense.reimburse", async (_, _, session) =>
        {
            if (!ValidText(errorCode, 100)) throw Failure("INVALID_BUSINESS_INPUT");
            var current = await session.RequireLeasedPayoutAsync(payoutId, version, leaseId, _clock.GetUtcNow(), ct);
            var next = current with
            {
                Version = Guid.NewGuid(), State = ExpensePayoutState.Failed,
                LeaseId = null, LeaseExpiresAt = null, ErrorCode = errorCode
            };
            await session.SavePayoutAsync(next, version, ct);
            await session.AppendAuditAsync(new(Guid.NewGuid(), session.Actor, "expense-payout", next.Id,
                "failed", version, next.Version, _clock.GetUtcNow()), ct);
            return next;
        }, ct);

    private sealed partial class Session
    {
        public async Task EnsureNoActivePayoutAsync(Guid expenseId, CancellationToken ct)
        {
            if (await FindActivePayoutByExpenseAsync(expenseId, ct) is not null)
                throw Failure("EXPENSE_PAYOUT_ACTIVE");
        }

        public Task<ExpensePayoutRequest?> FindPayoutAsync(Guid id, CancellationToken ct)
            => ReadPayoutAsync("PAYOUT_ID=@Key", Text(id), ct);

        public Task<ExpensePayoutRequest?> FindPayoutByOperationAsync(Guid operationId, CancellationToken ct)
            => ReadPayoutAsync("OPERATION_ID=@Key", Text(operationId), ct);

        public Task<ExpensePayoutRequest?> FindActivePayoutByExpenseAsync(Guid expenseId, CancellationToken ct)
            => ReadPayoutAsync("EXPENSE_ID=@Key AND STATE IN (0,1)", Text(expenseId), ct);

        private async Task<ExpensePayoutRequest?> ReadPayoutAsync(string predicate, string key, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_EXPENSE_PAYOUT WHERE "
                + ScopeWhere + " AND " + predicate, new { Key = key }, ct);
            return payload is null ? null : ValidatePayout(Deserialize<ExpensePayoutRequest>(payload));
        }

        public async Task SavePayoutAsync(ExpensePayoutRequest value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), Previous = Text(expectedVersion),
                Operation = Text(value.OperationId), Expense = Text(value.ExpenseId), State = (int)value.State,
                LeaseExpires = value.LeaseExpiresAt?.UtcTicks, Payload = Serialize(value)
            };
            await Write(expectedVersion is null ? """
                INSERT INTO ERP_EXPENSE_PAYOUT
                    (TENANT_ID,ORGANIZATION_ID,PAYOUT_ID,VERSION,OPERATION_ID,EXPENSE_ID,STATE,LEASE_EXPIRES_AT_TICKS,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@Expense,@State,@LeaseExpires,@Payload)
                """ : "UPDATE ERP_EXPENSE_PAYOUT SET VERSION=@Version,STATE=@State,LEASE_EXPIRES_AT_TICKS=@LeaseExpires,"
                    + "PAYLOAD=@Payload WHERE " + ScopeWhere + " AND PAYOUT_ID=@Id AND VERSION=@Previous",
                values, ct);
        }

        public async Task<BusinessPage<ExpensePayoutRequest>> QueryPayoutsAsync(
            Guid? expenseId, int offset, int limit, CancellationToken ct)
        {
            const string filter = " AND (@Expense IS NULL OR EXPENSE_ID=@Expense)";
            var values = new { Expense = Text(expenseId), Offset = offset, EndRow = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_EXPENSE_PAYOUT WHERE "
                + ScopeWhere + filter, values, ct);
            var rows = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM (SELECT PAYLOAD,"
                + "ROW_NUMBER() OVER (ORDER BY PAYOUT_ID) AS RowNumber FROM ERP_EXPENSE_PAYOUT WHERE "
                + ScopeWhere + filter + ") AS page WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber",
                values, ct);
            return new(Array.AsReadOnly(rows.Select(x => ValidatePayout(
                Deserialize<ExpensePayoutRequest>(x.Payload))).ToArray()), total);
        }

        public async Task<IReadOnlyList<ExpensePayoutRequest>> QueryDuePayoutsAsync(
            DateTimeOffset now, int limit, CancellationToken ct)
        {
            var rows = await Rows<PayloadRow>("""
                SELECT PAYLOAD AS Payload FROM (
                    SELECT PAYLOAD,ROW_NUMBER() OVER (ORDER BY PAYOUT_ID) AS RowNumber
                      FROM ERP_EXPENSE_PAYOUT
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                       AND (STATE=0 OR (STATE=1 AND LEASE_EXPIRES_AT_TICKS<=@Now))) AS due
                 WHERE RowNumber<=@Limit ORDER BY RowNumber
                """, new { Now = now.UtcTicks, Limit = limit }, ct);
            return Array.AsReadOnly(rows.Select(x => ValidatePayout(
                Deserialize<ExpensePayoutRequest>(x.Payload))).ToArray());
        }

        public async Task<ExpensePayoutRequest> RequireLeasedPayoutAsync(
            Guid id, Guid version, Guid leaseId, DateTimeOffset now, CancellationToken ct)
        {
            var current = await FindPayoutAsync(id, ct) ?? throw Failure("EXPENSE_PAYOUT_NOT_FOUND");
            if (current.Version != version) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (current.State != ExpensePayoutState.Processing || current.LeaseId != leaseId
                || current.LeaseExpiresAt is null || current.LeaseExpiresAt <= now)
                throw Failure("EXPENSE_PAYOUT_LEASE_INVALID");
            return current;
        }

        private ExpensePayoutRequest ValidatePayout(ExpensePayoutRequest value)
        {
            if (value.Scope != Scope || value.Id == Guid.Empty || value.Version == Guid.Empty
                || value.OperationId == Guid.Empty || value.ExpenseId == Guid.Empty
                || value.ExpenseVersion == Guid.Empty || value.EmployeeId == Guid.Empty
                || value.Amount <= 0 || decimal.Round(value.Amount, 6) != value.Amount
                || value.Currency.Length != 3 || value.Currency.Any(c => c is < 'A' or > 'Z')
                || !ValidText(value.ProviderKey, 100) || !ValidText(value.CreatedBy, 50)
                || value.AttemptCount < 0 || value.CreatedAt == default
                || value.CreatedAt.Offset != TimeSpan.Zero || !Enum.IsDefined(value.State))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            var leased = value.State == ExpensePayoutState.Processing;
            if (leased != value.LeaseId.HasValue || leased != value.LeaseExpiresAt.HasValue
                || value.LeaseExpiresAt.HasValue && value.LeaseExpiresAt.Value.Offset != TimeSpan.Zero)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            var completed = value.State == ExpensePayoutState.Completed;
            if (completed != (value.ProviderReference is not null) || completed != value.PaidAt.HasValue
                || value.ProviderReference is not null && !ValidText(value.ProviderReference, 255)
                || value.PaidAt.HasValue && value.PaidAt.Value.Offset != TimeSpan.Zero)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            var failed = value.State == ExpensePayoutState.Failed;
            if (failed != (value.ErrorCode is not null)
                || value.ErrorCode is not null && !ValidText(value.ErrorCode, 100))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            return value;
        }
    }
}
