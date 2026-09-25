using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;
using NexaOne.ServiceContracts.Hr;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    public Task<BillingTimeSource> RegisterTimeSourceAsync(string userId, Guid tenantId,
        Guid organizationId, Guid timeEntryId, BillingTimeSourceInput input,
        CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.write",
            (_, session) => session.RegisterTimeSource(timeEntryId, input, ct), ct);

    public Task<BillingTimeSource> GetTimeSourceAsync(string userId, Guid tenantId,
        Guid organizationId, Guid timeEntryId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.read",
            (_, session) => session.TimeSource(timeEntryId, required: true, ct), ct)!;

    private sealed partial class Session
    {
        internal async Task<BillingTimeSource> RegisterTimeSource(Guid timeEntryId,
            BillingTimeSourceInput input, CancellationToken ct)
        {
            if (timeEntryId == Guid.Empty || input is null || input.ContactId == Guid.Empty)
                throw Failure("INVALID_BUSINESS_INPUT");
            RequireText(input.Currency, 3); RequireText(input.Description, 255);
            if (input.Currency.Any(c => c is < 'A' or > 'Z') || input.HourlyRate < 0m
                || decimal.Round(input.HourlyRate, 6) != input.HourlyRate
                || input.HourlyRate > 1_000_000_000_000m)
                throw Failure("INVALID_BUSINESS_INPUT");
            if (!await ContactExistsAsync(input.ContactId, ct))
                throw Failure("BILLING_CONTACT_NOT_FOUND");

            var current = await TimeSource(timeEntryId, required: false, ct);
            if (current is not null)
            {
                if (current.ContactId != input.ContactId || current.Currency != input.Currency
                    || current.Description != input.Description || current.HourlyRate != input.HourlyRate
                    || current.ApplyTax != input.ApplyTax || current.ApplyDiscount != input.ApplyDiscount)
                    throw Failure("BILLING_TIME_SOURCE_CONFLICT");
                return current;
            }

            var occurrence = await TimeOccurrence(timeEntryId, ct);
            if (!occurrence.Approved) throw Failure("TIME_ENTRY_NOT_APPROVED");
            var hours = Hours(occurrence.DurationTicks);
            await Write("""
                INSERT INTO ERP_BILLING_TIME_SOURCE
                    (TENANT_ID,ORGANIZATION_ID,TIME_ENTRY_ID,CONTACT_ID,EMPLOYEE_ID,PROJECT_ID,TASK_ID,
                     OCCURRED_ON,DURATION_TICKS,
                     CURRENCY,DESCRIPTION,HOURLY_RATE,APPLY_TAX,APPLY_DISCOUNT,CREATED_BY,CREATED_AT_TICKS)
                VALUES (@TenantId,@OrganizationId,@TimeEntry,@Contact,@Employee,@Project,@Task,@OccurredOn,@Duration,
                        @Currency,@Description,@HourlyRate,@ApplyTax,@ApplyDiscount,@CreatedBy,@CreatedAt)
                """, new { TimeEntry = Text(timeEntryId), Contact = Text(input.ContactId),
                    Employee = Text(occurrence.EmployeeId),
                    Project = Text(occurrence.ProjectId), Task = Text(occurrence.TaskId),
                    OccurredOn = Day(occurrence.OccurredOn), Duration = occurrence.DurationTicks,
                    input.Currency, input.Description, HourlyRate = Amount(input.HourlyRate),
                    input.ApplyTax, input.ApplyDiscount, CreatedBy = Actor.UserId,
                    CreatedAt = clock.GetUtcNow().UtcTicks }, ct);
            var created = await TimeSource(timeEntryId, required: true, ct)
                ?? throw Failure("STORAGE_CONTRACT_VIOLATION");
            if (created.Hours != hours) throw Failure("STORAGE_CONTRACT_VIOLATION");
            return created;
        }

        internal async Task<BillingTimeSource?> TimeSource(Guid timeEntryId, bool required,
            CancellationToken ct)
        {
            if (timeEntryId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var row = await Row<TimeSourceRow>("""
                SELECT R.TIME_ENTRY_ID AS TimeEntryId,R.CONTACT_ID AS ContactId,
                       R.EMPLOYEE_ID AS EmployeeId,R.PROJECT_ID AS ProjectId,R.TASK_ID AS TaskId,
                       R.OCCURRED_ON AS OccurredOn,R.DURATION_TICKS AS DurationTicks,
                       R.CURRENCY AS Currency,R.DESCRIPTION AS Description,
                       R.HOURLY_RATE AS HourlyRate,R.APPLY_TAX AS ApplyTax,
                       R.APPLY_DISCOUNT AS ApplyDiscount,G.DOCUMENT_ID AS InvoiceId
                  FROM ERP_BILLING_TIME_SOURCE R
                  LEFT JOIN ERP_AUTOMATIC_BILLING_SOURCE S
                    ON S.TENANT_ID=R.TENANT_ID AND S.ORGANIZATION_ID=R.ORGANIZATION_ID
                   AND S.SOURCE_KIND=@SourceKind AND S.SOURCE_ID=R.TIME_ENTRY_ID
                  LEFT JOIN ERP_AUTOMATIC_BILLING_GENERATION G
                    ON G.TENANT_ID=S.TENANT_ID AND G.ORGANIZATION_ID=S.ORGANIZATION_ID
                   AND G.GENERATION_ID=S.GENERATION_ID
                 WHERE R.TENANT_ID=@TenantId AND R.ORGANIZATION_ID=@OrganizationId
                   AND R.TIME_ENTRY_ID=@TimeEntry
                """, new { SourceKind = (int)BillingSourceKind.TimeEntry,
                    TimeEntry = Text(timeEntryId) }, ct);
            if (row is null)
            {
                if (required) throw Failure("BILLING_TIME_SOURCE_NOT_FOUND");
                return null;
            }
            var occurrence = await TimeOccurrence(timeEntryId, ct);
            if (!occurrence.Approved || occurrence.OccurredOn != Day(row.OccurredOn)
                || occurrence.DurationTicks != row.DurationTicks
                || occurrence.EmployeeId != Id(row.EmployeeId)
                || occurrence.ProjectId != OptionalId(row.ProjectId)
                || occurrence.TaskId != OptionalId(row.TaskId))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            return new(timeEntryId, occurrence.EmployeeId, occurrence.ProjectId, occurrence.TaskId,
                Id(row.ContactId), occurrence.OccurredOn, row.Currency, row.Description,
                Amount(row.HourlyRate), Hours(row.DurationTicks), row.ApplyTax, row.ApplyDiscount,
                row.InvoiceId is null ? BillingTimeSourceState.Eligible : BillingTimeSourceState.Invoiced,
                OptionalId(row.InvoiceId));
        }

        private async Task<TimeBillingOccurrence> TimeOccurrence(
            Guid timeEntryId, CancellationToken ct)
        {
            if (timeBilling is null) throw Failure("TIME_ENTRY_NOT_FOUND");
            return await timeBilling.GetTimeEntryInTransactionAsync(transaction,
                Id(Scope.TenantId), Id(Scope.OrganizationId), timeEntryId, ct)
                ?? throw Failure("TIME_ENTRY_NOT_FOUND");
        }

        private static decimal Hours(long ticks)
        {
            if (ticks <= 0) throw Failure("STORAGE_CONTRACT_VIOLATION");
            var hours = decimal.Round((decimal)ticks / TimeSpan.TicksPerHour, 6,
                MidpointRounding.AwayFromZero);
            if (hours <= 0m) throw Failure("TIME_ENTRY_TOO_SHORT_TO_BILL");
            return hours;
        }

        private sealed class TimeSourceRow
        {
            public string TimeEntryId { get; set; } = "";
            public string ContactId { get; set; } = "";
            public string EmployeeId { get; set; } = "";
            public string? ProjectId { get; set; }
            public string? TaskId { get; set; }
            public string OccurredOn { get; set; } = "";
            public long DurationTicks { get; set; }
            public string Currency { get; set; } = "";
            public string Description { get; set; } = "";
            public string HourlyRate { get; set; } = "";
            public bool ApplyTax { get; set; }
            public bool ApplyDiscount { get; set; }
            public string? InvoiceId { get; set; }
        }
    }
}
