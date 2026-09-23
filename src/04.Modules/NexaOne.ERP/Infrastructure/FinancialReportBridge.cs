using System.Data;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge : IFinancialReportBridge
{
    private const int MaxFinancialReportSourceRows = 100_000;

    Task<BusinessPage<BusinessMembership>> IFinancialReportBridge.ListAccessibleScopesAsync(
        string userId, int offset, int limit, CancellationToken ct)
    {
        if (!ValidText(userId, 50)) throw Failure("BUSINESS_ACCESS_DENIED");
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return _processor.ExecuteInTransactionAsync(async (_, transaction) =>
        {
            const int batchSize = 128;
            var items = new List<BusinessMembership>(limit);
            long total = 0;
            Guid? afterTenant = null, afterOrganization = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
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
                    if (membership is null || membership.TenantId == Guid.Empty
                        || membership.OrganizationId == Guid.Empty || membership.BusinessUserId == Guid.Empty
                        || !membership.IsActive || membership.Version <= 0 || membership.Permissions is null)
                        throw Failure("STORAGE_CONTRACT_VIOLATION");
                    if (afterTenant.HasValue)
                    {
                        var tenantOrder = string.CompareOrdinal(Text(membership.TenantId), Text(afterTenant.Value));
                        if (tenantOrder < 0 || tenantOrder == 0
                            && string.CompareOrdinal(Text(membership.OrganizationId), Text(afterOrganization!.Value)) <= 0)
                            throw Failure("STORAGE_CONTRACT_VIOLATION");
                    }
                    afterTenant = membership.TenantId;
                    afterOrganization = membership.OrganizationId;
                    if (!membership.Permissions.Contains("financial-report.read", StringComparer.Ordinal)) continue;
                    if (total++ >= offset && items.Count < limit) items.Add(membership);
                }
                if (batch.Count < batchSize) break;
            }
            ct.ThrowIfCancellationRequested();
            return new BusinessPage<BusinessMembership>(Array.AsReadOnly(items.ToArray()), total);
        }, IsolationLevel.Serializable, ct);
    }

    public Task<FinancialReport> BuildAsync(string userId, Guid tenantId, Guid organizationId,
        FinancialReportPeriod period, CancellationToken ct = default)
        => RunFinancialReport(userId, tenantId, organizationId,
            (service, session) => service.BuildAsync(session.Actor, period, ct), ct);

    public Task<string> ExportCsvAsync(string userId, Guid tenantId, Guid organizationId,
        FinancialReportPeriod period, CancellationToken ct = default)
        => RunFinancialReport(userId, tenantId, organizationId, async (service, session) =>
            FinancialReportService.ExportCsv(await service.BuildAsync(session.Actor, period, ct)), ct);

    private Task<T> RunFinancialReport<T>(string userId, Guid tenantId, Guid organizationId,
        Func<FinancialReportService, Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock);
            try
            {
                await session.Authorize(userId, "financial-report.read", ct);
                var result = await action(new FinancialReportService(session, session, _clock), session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private sealed partial class Session : IAtomicBusinessStore<IFinancialReportTransaction>, IFinancialReportTransaction
    {
        async Task<T> IAtomicBusinessStore<IFinancialReportTransaction>.ExecuteAsync<T>(BusinessScope requestedScope,
            Func<IFinancialReportTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            EnsureExecution(requestedScope, ct);
            var result = await work(this, ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }

        public async Task<FinancialReportSource> ReadFinancialReportSourceAsync(
            DateOnly start, DateOnly end, CancellationToken ct)
        {
            var values = new { Start = Day(start), End = Day(end) };
            var invoiceCount = await Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_DOCUMENT WHERE "
                + ScopeWhere + " AND KIND=1 AND STATUS IN (1,4,5,6) AND DOCUMENT_DATE>=@Start AND DOCUMENT_DATE<=@End", values, ct);
            var incomeCount = await Scalar<long>("SELECT COUNT(*) FROM ERP_INCOME WHERE "
                + ScopeWhere + " AND STATE=0 AND VALUE_DATE>=@Start AND VALUE_DATE<=@End", values, ct);
            var expenseCount = await Scalar<long>("SELECT COUNT(*) FROM ERP_EXPENSE WHERE "
                + ScopeWhere + " AND STATE=0 AND VALUE_DATE>=@Start AND VALUE_DATE<=@End", values, ct);
            if (invoiceCount < 0 || incomeCount < 0 || expenseCount < 0
                || invoiceCount > MaxFinancialReportSourceRows
                || incomeCount > MaxFinancialReportSourceRows - invoiceCount
                || expenseCount > MaxFinancialReportSourceRows - invoiceCount - incomeCount)
                throw Failure("FINANCIAL_REPORT_TOO_LARGE");

            var documentRows = await Rows<DocumentRow>("SELECT " + DocumentColumns
                + " FROM ERP_BILLING_DOCUMENT WHERE " + ScopeWhere
                + " AND KIND=1 AND STATUS IN (1,4,5,6) AND DOCUMENT_DATE>=@Start AND DOCUMENT_DATE<=@End"
                + " ORDER BY DOCUMENT_DATE,DOCUMENT_ID", values, ct);
            var lines = documentRows.Length == 0
                ? new Dictionary<string, List<BillingLine>>(StringComparer.Ordinal)
                : await ReadFinancialReportLines(values, ct);
            var invoices = documentRows.Select(row =>
                Document(row, lines.TryGetValue(row.Id, out var own) ? own : [])).ToArray();

            var incomeRows = await Rows<FinancialPayloadRow>("SELECT PAYLOAD AS Payload FROM ERP_INCOME WHERE "
                + ScopeWhere + " AND STATE=0 AND VALUE_DATE>=@Start AND VALUE_DATE<=@End"
                + " ORDER BY VALUE_DATE,INCOME_ID", values, ct);
            var expenseRows = await Rows<FinancialPayloadRow>("SELECT PAYLOAD AS Payload FROM ERP_EXPENSE WHERE "
                + ScopeWhere + " AND STATE=0 AND VALUE_DATE>=@Start AND VALUE_DATE<=@End"
                + " ORDER BY VALUE_DATE,EXPENSE_ID", values, ct);

            if (documentRows.LongLength != invoiceCount || incomeRows.LongLength != incomeCount
                || expenseRows.LongLength != expenseCount)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            return new(Array.AsReadOnly(invoices),
                Array.AsReadOnly(incomeRows.Select(row => Deserialize<IncomeRecord>(row.Payload)).ToArray()),
                Array.AsReadOnly(expenseRows.Select(row => Deserialize<ExpenseRecord>(row.Payload)).ToArray()));
        }

        private async Task<Dictionary<string, List<BillingLine>>> ReadFinancialReportLines(
            object values, CancellationToken ct)
        {
            var rows = await Rows<FinancialLineRow>("""
                SELECT l.DOCUMENT_ID AS DocumentId, l.LINE_NO AS LineNumber,
                       l.DESCRIPTION AS Description, l.UNIT_PRICE AS UnitPrice,
                       l.QUANTITY AS Quantity, l.APPLY_TAX AS ApplyTax,
                       l.APPLY_DISCOUNT AS ApplyDiscount, l.EXPENSE_ID AS ExpenseId
                  FROM ERP_BILLING_LINE l
                  JOIN ERP_BILLING_DOCUMENT d
                    ON d.TENANT_ID=l.TENANT_ID AND d.ORGANIZATION_ID=l.ORGANIZATION_ID
                   AND d.DOCUMENT_ID=l.DOCUMENT_ID
                 WHERE d.TENANT_ID=@TenantId AND d.ORGANIZATION_ID=@OrganizationId
                   AND d.KIND=1 AND d.STATUS IN (1,4,5,6)
                   AND d.DOCUMENT_DATE>=@Start AND d.DOCUMENT_DATE<=@End
                 ORDER BY l.DOCUMENT_ID,l.LINE_NO
                """, values, ct);
            var result = new Dictionary<string, List<BillingLine>>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                if (!result.TryGetValue(row.DocumentId, out var list))
                    result[row.DocumentId] = list = [];
                if (row.LineNumber != list.Count + 1)
                    throw new InvalidDataException("Billing storage contains a gap in document lines.");
                list.Add(new(row.Description, Amount(row.UnitPrice), Amount(row.Quantity),
                    row.ApplyTax, row.ApplyDiscount, OptionalId(row.ExpenseId)));
            }
            return result;
        }
    }

    private sealed class FinancialPayloadRow
    {
        public string Payload { get; set; } = "";
    }

    private sealed class FinancialLineRow
    {
        public string DocumentId { get; set; } = "";
        public int LineNumber { get; set; }
        public string Description { get; set; } = "";
        public string UnitPrice { get; set; } = "";
        public string Quantity { get; set; } = "";
        public bool ApplyTax { get; set; }
        public bool ApplyDiscount { get; set; }
        public string? ExpenseId { get; set; }
    }
}
