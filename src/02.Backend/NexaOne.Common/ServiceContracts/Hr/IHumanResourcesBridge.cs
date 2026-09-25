using System.Data.Common;
using NexaFramework.Service.HumanResources;

namespace NexaOne.ServiceContracts.Hr;

public sealed record HrTask(Guid Id, Guid Version, string Name, Guid? ProjectId, bool Active);

public sealed record HrTaskInput(string Name, Guid? ProjectId = null);

public sealed record ManualTimeInput(DateTimeOffset Start, DateTimeOffset End,
    Guid? ProjectId = null, Guid? TaskId = null, string Description = "");

/// <summary>Approved HR-owned occurrence consumed by ERP inside the caller's transaction.</summary>
public sealed record TimeBillingOccurrence(Guid TimeEntryId, Guid EmployeeId, DateOnly OccurredOn,
    long DurationTicks, string Description, Guid? ProjectId, Guid? TaskId, bool Approved);

public interface ITimeBillingDirectory : INexaModuleBridge
{
    Task<TimeBillingOccurrence?> GetTimeEntryInTransactionAsync(DbTransaction transaction,
        Guid tenantId, Guid organizationId, Guid timeEntryId, CancellationToken ct = default);

    Task<IReadOnlyList<TimeBillingOccurrence>> FindTimeEntriesInTransactionAsync(DbTransaction transaction,
        Guid tenantId, Guid organizationId, IReadOnlyList<Guid> timeEntryIds,
        CancellationToken ct = default);
}

/// <summary>Organization-scoped manual time, timer, task and timesheet lifecycle.</summary>
public interface IHumanResourcesBridge : INexaModuleBridge
{
    Task<HrTask> CreateTaskAsync(string userId, Guid tenantId, Guid organizationId,
        HrTaskInput input, CancellationToken ct = default);
    Task<TimeEntry> StartTimerAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? projectId = null, Guid? taskId = null, string description = "",
        CancellationToken ct = default);
    Task<TimeEntry> StopTimerAsync(string userId, Guid tenantId, Guid organizationId,
        Guid entryId, Guid version, CancellationToken ct = default);
    Task<TimeEntry> RecordTimeAsync(string userId, Guid tenantId, Guid organizationId,
        ManualTimeInput input, CancellationToken ct = default);
    Task<TimeEntry> CorrectTimeAsync(string userId, Guid tenantId, Guid organizationId,
        Guid entryId, Guid version, DateTimeOffset start, DateTimeOffset end, string reason,
        CancellationToken ct = default);
    Task<Timesheet> SubmitTimesheetAsync(string userId, Guid tenantId, Guid organizationId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default);
    Task<Timesheet> ReviewTimesheetAsync(string userId, Guid tenantId, Guid organizationId,
        Guid timesheetId, Guid version, bool approve, string reason,
        CancellationToken ct = default);
    Task<TimeEntry> GetTimeEntryAsync(string userId, Guid tenantId, Guid organizationId,
        Guid entryId, CancellationToken ct = default);
    Task<Timesheet> GetTimesheetAsync(string userId, Guid tenantId, Guid organizationId,
        Guid timesheetId, CancellationToken ct = default);
}
