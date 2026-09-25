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
            if (request.InvoiceType is BillingInvoiceType.ByEmployeeHours
                or BillingInvoiceType.ByProjectHours or BillingInvoiceType.ByTaskHours)
            {
                if (timeBilling is null) return Array.Empty<AutomaticBillingSource>();
                var relation = request.InvoiceType switch
                {
                    BillingInvoiceType.ByProjectHours => " AND R.PROJECT_ID IS NOT NULL",
                    BillingInvoiceType.ByTaskHours => " AND R.TASK_ID IS NOT NULL",
                    _ => string.Empty
                };
                var timeRows = await Rows<TimeBillingRow>("""
                    SELECT TIME_ENTRY_ID AS TimeEntryId,CONTACT_ID AS ContactId,
                           OCCURRED_ON AS OccurredOn,DURATION_TICKS AS DurationTicks,
                           CURRENCY AS Currency,DESCRIPTION AS Description,HOURLY_RATE AS HourlyRate,
                           APPLY_TAX AS ApplyTax,APPLY_DISCOUNT AS ApplyDiscount
                      FROM (SELECT R.*,
                           ROW_NUMBER() OVER (ORDER BY R.OCCURRED_ON,R.TIME_ENTRY_ID) AS RowNumber
                              FROM ERP_BILLING_TIME_SOURCE R
                             WHERE R.TENANT_ID=@TenantId AND R.ORGANIZATION_ID=@OrganizationId
                               AND R.CONTACT_ID=@Contact AND R.CURRENCY=@Currency
                               AND R.OCCURRED_ON>=@Start AND R.OCCURRED_ON<=@End
                    """ + relation + """
                               AND NOT EXISTS (SELECT 1 FROM ERP_AUTOMATIC_BILLING_SOURCE S
                                 WHERE S.TENANT_ID=R.TENANT_ID AND S.ORGANIZATION_ID=R.ORGANIZATION_ID
                                   AND S.SOURCE_KIND=@SourceKind AND S.SOURCE_ID=R.TIME_ENTRY_ID)
                           ) page WHERE RowNumber<=@EndRow ORDER BY RowNumber
                    """, new { SourceKind = (int)BillingSourceKind.TimeEntry,
                        Contact = Text(request.ContactId), request.Currency,
                        Start = Day(request.PeriodStart), End = Day(request.PeriodEnd), EndRow = 201 }, ct);
                if (timeRows.Length == 0) return Array.Empty<AutomaticBillingSource>();
                var ids = timeRows.Select(row => Id(row.TimeEntryId)).ToArray();
                var occurrences = await timeBilling.FindTimeEntriesInTransactionAsync(transaction,
                    Id(Scope.TenantId), Id(Scope.OrganizationId), ids, ct);
                var byId = occurrences.ToDictionary(value => value.TimeEntryId);
                var timeSources = new AutomaticBillingSource[timeRows.Length];
                for (var index = 0; index < timeRows.Length; index++)
                {
                    var row = timeRows[index];
                    var id = Id(row.TimeEntryId);
                    if (!byId.TryGetValue(id, out var occurrence) || !occurrence.Approved
                        || occurrence.OccurredOn != Day(row.OccurredOn)
                        || occurrence.DurationTicks != row.DurationTicks)
                        throw Failure("STORAGE_CONTRACT_VIOLATION");
                    timeSources[index] = new(id, BillingSourceKind.TimeEntry, occurrence.OccurredOn,
                        Id(row.ContactId), row.Currency, row.Description, Amount(row.HourlyRate),
                        TimeHours(row.DurationTicks), row.ApplyTax, row.ApplyDiscount,
                        EmployeeId: occurrence.EmployeeId, ProjectId: occurrence.ProjectId,
                        TaskId: occurrence.TaskId);
                }
                return Array.AsReadOnly(timeSources);
            }
            if (request.InvoiceType == BillingInvoiceType.ByProducts)
            {
                if (stockBilling is null) return Array.Empty<AutomaticBillingSource>();
                const int batchSize = 128;
                var productSources = new List<AutomaticBillingSource>(201);
                string? afterDay = null, afterMovement = null;
                while (productSources.Count < 201)
                {
                    var productRows = await Rows<ProductBillingRow>("""
                        SELECT MovementId,OccurredOn,ContactId,Currency,Description,UnitPrice,
                               ApplyTax,ApplyDiscount FROM (
                        SELECT R.MOVEMENT_ID AS MovementId, R.OCCURRED_ON AS OccurredOn,
                               R.CONTACT_ID AS ContactId, R.CURRENCY AS Currency,
                               R.DESCRIPTION AS Description, R.UNIT_PRICE AS UnitPrice,
                               R.APPLY_TAX AS ApplyTax, R.APPLY_DISCOUNT AS ApplyDiscount,
                               ROW_NUMBER() OVER (ORDER BY R.OCCURRED_ON,R.MOVEMENT_ID) AS RowNumber
                          FROM ERP_BILLING_PRODUCT_SOURCE R
                         WHERE R.TENANT_ID=@TenantId AND R.ORGANIZATION_ID=@OrganizationId
                           AND R.CONTACT_ID=@Contact AND R.CURRENCY=@Currency
                           AND R.OCCURRED_ON>=@Start AND R.OCCURRED_ON<=@End
                           AND (@AfterDay IS NULL OR R.OCCURRED_ON>@AfterDay
                             OR (R.OCCURRED_ON=@AfterDay AND R.MOVEMENT_ID>@AfterMovement))
                           AND NOT EXISTS (SELECT 1 FROM ERP_AUTOMATIC_BILLING_SOURCE S
                             WHERE S.TENANT_ID=R.TENANT_ID AND S.ORGANIZATION_ID=R.ORGANIZATION_ID
                               AND S.SOURCE_KIND=@SourceKind AND S.SOURCE_ID=R.MOVEMENT_ID)
                        ) page WHERE RowNumber<=@BatchSize ORDER BY RowNumber
                        """, new { SourceKind = (int)BillingSourceKind.Product,
                            Contact = Text(request.ContactId), request.Currency,
                            Start = Day(request.PeriodStart), End = Day(request.PeriodEnd),
                            AfterDay = afterDay, AfterMovement = afterMovement, BatchSize = batchSize }, ct);
                    if (productRows.Length == 0) break;
                    var ids = productRows.Select(row => Id(row.MovementId)).ToArray();
                    var occurrences = await stockBilling.FindMovementsInTransactionAsync(transaction,
                        Id(Scope.TenantId), Id(Scope.OrganizationId), ids, ct);
                    var byId = occurrences.ToDictionary(value => value.MovementId);
                    foreach (var row in productRows)
                    {
                        var movementId = Id(row.MovementId);
                        if (!byId.TryGetValue(movementId, out var occurrence) || !occurrence.BillableIssue
                            || occurrence.OccurredOn != Day(row.OccurredOn))
                            throw Failure("STORAGE_CONTRACT_VIOLATION");
                        if (occurrence.Reversed) continue;
                        productSources.Add(new(movementId, BillingSourceKind.Product, occurrence.OccurredOn,
                            Id(row.ContactId), row.Currency, row.Description, Amount(row.UnitPrice),
                            occurrence.Quantity, row.ApplyTax, row.ApplyDiscount,
                            ProductId: occurrence.ProductId));
                        if (productSources.Count == 201) break;
                    }
                    afterDay = productRows[^1].OccurredOn;
                    afterMovement = productRows[^1].MovementId;
                    if (productRows.Length < batchSize) break;
                }
                productSources.Sort(static (left, right) =>
                {
                    var date = left.OccurredOn.CompareTo(right.OccurredOn);
                    return date != 0 ? date : left.Id.CompareTo(right.Id);
                });
                return Array.AsReadOnly(productSources.ToArray());
            }
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

        private sealed class ProductBillingRow
        {
            public string MovementId { get; set; } = "";
            public string OccurredOn { get; set; } = "";
            public string ContactId { get; set; } = "";
            public string Currency { get; set; } = "";
            public string Description { get; set; } = "";
            public string UnitPrice { get; set; } = "";
            public bool ApplyTax { get; set; }
            public bool ApplyDiscount { get; set; }
        }

        private static decimal TimeHours(long ticks)
        {
            if (ticks <= 0) throw Failure("STORAGE_CONTRACT_VIOLATION");
            var hours = decimal.Round((decimal)ticks / TimeSpan.TicksPerHour, 6,
                MidpointRounding.AwayFromZero);
            return hours > 0m ? hours : throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        private sealed class TimeBillingRow
        {
            public string TimeEntryId { get; set; } = "";
            public string ContactId { get; set; } = "";
            public string OccurredOn { get; set; } = "";
            public long DurationTicks { get; set; }
            public string Currency { get; set; } = "";
            public string Description { get; set; } = "";
            public string HourlyRate { get; set; } = "";
            public bool ApplyTax { get; set; }
            public bool ApplyDiscount { get; set; }
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
