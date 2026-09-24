using NexaFramework.Service;
using NexaFramework.Service.Erp;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    public Task<AutomaticBillingResult> GenerateAutomaticInvoiceAsync(string userId, Guid tenantId,
        Guid organizationId, Guid operationId, AutomaticBillingRequest request, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write", (_, session) =>
            new AutomaticBillingService(session, session, _clock).GenerateInvoiceAsync(
                session.Actor, operationId, request, ct), ct);

    private sealed partial class Session : IAtomicBusinessStore<IAutomaticBillingTransaction>,
        IAutomaticBillingTransaction
    {
        async Task<T> IAtomicBusinessStore<IAutomaticBillingTransaction>.ExecuteAsync<T>(BusinessScope requestedScope,
            Func<IAutomaticBillingTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            EnsureExecution(requestedScope, ct);
            var result = await work(this, ct); ct.ThrowIfCancellationRequested(); return result;
        }

        public async Task<AutomaticBillingGeneration?> FindAutomaticBillingGenerationByOperationAsync(
            Guid operationId, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM ERP_AUTOMATIC_BILLING_GENERATION WHERE "
                + ScopeWhere + " AND OPERATION_ID=@Operation", new { Operation = Text(operationId) }, ct);
            return payload is null ? null : Deserialize<AutomaticBillingGeneration>(payload);
        }

        public async Task<IReadOnlyList<AutomaticBillingSource>> QueryUnbilledSourcesAsync(
            AutomaticBillingRequest request, CancellationToken ct)
        {
            if (request.InvoiceType != BillingInvoiceType.DetailedItems)
                return Array.Empty<AutomaticBillingSource>();
            var values = new
            {
                Type = (int)ExpenseType.BillableToContact,
                SourceKind = (int)BillingSourceKind.Expense,
                Status = (int)ExpenseStatus.Uninvoiced,
                State = (int)ExpenseState.Active,
                Contact = Text(request.ContactId),
                request.Currency,
                Start = Day(request.PeriodStart),
                End = Day(request.PeriodEnd),
                EndRow = 201
            };
            var rows = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM (SELECT E.PAYLOAD,"
                + "ROW_NUMBER() OVER (ORDER BY E.VALUE_DATE,E.EXPENSE_ID) AS RowNumber FROM ERP_EXPENSE AS E WHERE "
                + "E.TENANT_ID=@TenantId AND E.ORGANIZATION_ID=@OrganizationId"
                + " AND E.TYPE=@Type AND E.STATUS=@Status AND E.STATE=@State AND E.CONTACT_ID=@Contact"
                + " AND E.CURRENCY=@Currency AND E.VALUE_DATE>=@Start AND E.VALUE_DATE<=@End"
                + " AND NOT EXISTS (SELECT 1 FROM ERP_AUTOMATIC_BILLING_SOURCE AS S"
                + " WHERE S.TENANT_ID=E.TENANT_ID AND S.ORGANIZATION_ID=E.ORGANIZATION_ID"
                + " AND S.SOURCE_KIND=@SourceKind AND S.SOURCE_ID=E.EXPENSE_ID)) AS page"
                + " WHERE RowNumber<=@EndRow ORDER BY RowNumber", values, ct);
            var sources = new AutomaticBillingSource[rows.Length];
            for (var index = 0; index < rows.Length; index++)
            {
                var expense = Deserialize<ExpenseRecord>(rows[index].Payload);
                var description = ValidText(expense.Input.Purpose, 255) ? expense.Input.Purpose!
                    : ValidText(expense.Input.Reference, 255) ? expense.Input.Reference!
                    : "Expense " + Text(expense.Id);
                sources[index] = new(expense.Id, BillingSourceKind.Expense, expense.Input.ValueDate,
                    expense.Input.ContactId!.Value, expense.Input.Currency, description,
                    expense.Amounts.Gross, 1m, ApplyTax: false, ApplyDiscount: false,
                    EmployeeId: expense.Input.EmployeeId, ProjectId: expense.Input.ProjectId,
                    ExpenseId: expense.Id);
            }
            return Array.AsReadOnly(sources);
        }

        public async Task SaveAutomaticBillingGenerationAsync(AutomaticBillingGeneration value,
            CancellationToken ct)
        {
            RequireScope(value.Scope);
            await Write("""
                INSERT INTO ERP_AUTOMATIC_BILLING_GENERATION
                    (TENANT_ID,ORGANIZATION_ID,GENERATION_ID,VERSION,OPERATION_ID,DOCUMENT_ID,INVOICE_TYPE,CREATED_BY,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@Document,@InvoiceType,@CreatedBy,@Payload)
                """, new { Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId),
                    Document = Text(value.DocumentId), InvoiceType = (int)value.Request.InvoiceType,
                    value.CreatedBy, Payload = Serialize(value) }, ct);
            foreach (var source in value.Sources)
            {
                await Write("""
                    INSERT INTO ERP_AUTOMATIC_BILLING_SOURCE
                        (TENANT_ID,ORGANIZATION_ID,SOURCE_KIND,SOURCE_ID,GENERATION_ID,LINE_NO,
                         EMPLOYEE_ID,PROJECT_ID,TASK_ID,PRODUCT_ID,EXPENSE_ID)
                    VALUES (@TenantId,@OrganizationId,@Kind,@Source,@Generation,@Line,
                            @Employee,@Project,@Task,@Product,@Expense)
                    """, new { Kind = (int)source.Kind, Source = Text(source.SourceId),
                        Generation = Text(value.Id), Line = source.LineNumber, Employee = Text(source.EmployeeId),
                        Project = Text(source.ProjectId), Task = Text(source.TaskId), Product = Text(source.ProductId),
                        Expense = Text(source.ExpenseId) }, ct);
                if (source.Kind != BillingSourceKind.Expense || !source.ExpenseId.HasValue)
                    continue;
                var expense = await FindExpenseAsync(source.ExpenseId.Value, ct)
                    ?? throw Failure("EXPENSE_NOT_FOUND");
                if (expense.State != ExpenseState.Active || expense.Input.Type != ExpenseType.BillableToContact
                    || expense.Status != ExpenseStatus.Uninvoiced || expense.Input.ContactId != value.Request.ContactId
                    || expense.Input.Currency != value.Request.Currency)
                    throw Failure("EXPENSE_NOT_INVOICEABLE");
                var linked = expense with { Version = Guid.NewGuid(), Status = ExpenseStatus.Invoiced,
                    InvoiceId = value.DocumentId, InvoiceOperationId = value.OperationId,
                    UnlinkedInvoiceId = null, InvoiceUnlinkOperationId = null };
                await SaveExpenseAsync(linked, expense.Version, ct);
                await AppendAuditAsync(new(Guid.NewGuid(), Actor, "expense", expense.Id,
                    "automatic-invoice-linked", expense.Version, linked.Version, clock.GetUtcNow()), ct);
            }
        }
    }
}
