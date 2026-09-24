using System.Data;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge : IBusinessReportBridge
{
    private const string FinancialActivityReport = "erp.financial-activity";
    private static readonly IReadOnlyList<BusinessReportDefinition> BusinessReports =
        Array.AsReadOnly(new[]
        {
            new BusinessReportDefinition(FinancialActivityReport, "financial-report.read",
                Array.AsReadOnly(new[]
                {
                    BusinessReportUnit.Total, BusinessReportUnit.Day, BusinessReportUnit.Month
                }))
        });

    Task<BusinessPage<BusinessMembership>> IBusinessReportBridge.ListAccessibleScopesAsync(
        string userId, int offset, int limit, CancellationToken ct)
        => ((IFinancialReportBridge)this).ListAccessibleScopesAsync(userId, offset, limit, ct);

    public Task<IReadOnlyList<BusinessReportDefinition>> ListAsync(string userId, Guid tenantId,
        Guid organizationId, CancellationToken ct = default)
        => RunBusinessReport(userId, tenantId, organizationId,
            (service, session) => service.ListAsync(session.Actor, ct), ct);

    public Task<BusinessReport> BuildAsync(string userId, Guid tenantId, Guid organizationId,
        string reportKey, BusinessReportPeriod period, BusinessReportUnit unit,
        CancellationToken ct = default)
        => RunBusinessReport(userId, tenantId, organizationId,
            (service, session) => service.BuildAsync(session.Actor, reportKey, period, unit, ct), ct);

    public Task<string> ExportCsvAsync(string userId, Guid tenantId, Guid organizationId,
        string reportKey, BusinessReportPeriod period, BusinessReportUnit unit,
        CancellationToken ct = default)
        => RunBusinessReport(userId, tenantId, organizationId, async (service, session) =>
            ReportService.ExportCsv(await service.BuildAsync(session.Actor, reportKey, period, unit, ct)), ct);

    private Task<T> RunBusinessReport<T>(string userId, Guid tenantId, Guid organizationId,
        Func<ReportService, Session, Task<T>> action, CancellationToken ct)
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
                var result = await action(new ReportService(session, session, BusinessReports, _clock), session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private sealed partial class Session : IAtomicBusinessStore<IReportTransaction>, IReportTransaction
    {
        async Task<T> IAtomicBusinessStore<IReportTransaction>.ExecuteAsync<T>(BusinessScope requestedScope,
            Func<IReportTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            EnsureExecution(requestedScope, ct);
            var result = await work(this, ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }

        public async Task<IReadOnlyList<BusinessReportSourceRow>> ReadBusinessReportSourceAsync(
            string reportKey, DateOnly start, DateOnly end, int limit, CancellationToken ct)
        {
            if (reportKey != FinancialActivityReport || limit < 1)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            var source = await ReadFinancialReportSourceAsync(start, end, ct);
            var rows = new List<BusinessReportSourceRow>(Math.Min(limit, 1024));
            foreach (var invoice in source.Invoices)
            {
                RequireInvoice(invoice, start, end);
                if (!Add(rows, limit, invoice.Input.DocumentDate, invoice.Input.Currency,
                    "invoice.invoiced", invoice.Totals.Total)) break;
                if (!Add(rows, limit, invoice.Input.DocumentDate, invoice.Input.Currency,
                    "invoice.paid", invoice.Paid)) break;
            }
            if (rows.Count < limit)
                foreach (var income in source.Incomes)
                {
                    RequireIncome(income, start, end);
                    if (!Add(rows, limit, income.Input.ValueDate, income.Input.Currency,
                        "income.received", income.Input.Amount)) break;
                }
            if (rows.Count < limit)
                foreach (var expense in source.Expenses)
                {
                    RequireExpense(expense, start, end);
                    if (!Add(rows, limit, expense.Input.ValueDate, expense.Input.Currency,
                        "expense.gross", expense.Amounts.Gross)
                        || !Add(rows, limit, expense.Input.ValueDate, expense.Input.Currency,
                            "expense.net", expense.Amounts.Net)
                        || !Add(rows, limit, expense.Input.ValueDate, expense.Input.Currency,
                            "expense.tax", expense.Amounts.Tax)) break;
                }
            return Array.AsReadOnly(rows.ToArray());
        }

        private static bool Add(List<BusinessReportSourceRow> rows, int limit, DateOnly date,
            string currency, string measure, decimal value)
        {
            if (rows.Count >= limit) return false;
            rows.Add(new(date, currency, measure, value));
            return rows.Count < limit;
        }

        private void RequireInvoice(BillingDocument value, DateOnly start, DateOnly end)
        {
            if (value is null || value.Scope != Scope || value.Input is null || value.Totals is null
                || value.Kind != BillingKind.Invoice
                || value.Status is not (BillingStatus.Sent or BillingStatus.PartiallyPaid
                    or BillingStatus.FullyPaid or BillingStatus.Overpaid)
                || !InPeriod(value.Input.DocumentDate, start, end) || !Currency(value.Input.Currency)
                || value.Totals.Total < 0m || value.Paid < 0m)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        private void RequireIncome(IncomeRecord value, DateOnly start, DateOnly end)
        {
            if (value is null || value.Scope != Scope || value.Input is null
                || value.State != IncomeState.Active
                || !InPeriod(value.Input.ValueDate, start, end) || !Currency(value.Input.Currency)
                || value.Input.Amount <= 0m)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        private void RequireExpense(ExpenseRecord value, DateOnly start, DateOnly end)
        {
            if (value is null || value.Scope != Scope || value.Input is null || value.Amounts is null
                || value.State != ExpenseState.Active
                || !InPeriod(value.Input.ValueDate, start, end) || !Currency(value.Input.Currency)
                || value.Amounts.Gross < 0m || value.Amounts.Tax < 0m || value.Amounts.Net < 0m
                || value.Amounts.Gross != value.Amounts.Tax + value.Amounts.Net)
                throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        private static bool InPeriod(DateOnly value, DateOnly start, DateOnly end)
            => value >= start && value <= end;
        private static bool Currency(string value)
            => value is { Length: 3 } && value.All(character => character is >= 'A' and <= 'Z');
    }
}
