using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using NexaFramework.Service;
using NexaFramework.Service.HumanResources;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Hr;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.HR.Infrastructure;

public sealed class TimeTrackingBridge : IHumanResourcesBridge, ITimeBillingDirectory
{
    private const string ScopeWhere = "TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ServiceObjectProcessor _processor;
    private readonly int? _timeout;
    private readonly IBusinessMembershipBridge _memberships;
    private readonly IBusinessProjectDirectory _projects;
    private readonly TimeProvider _clock;

    public TimeTrackingBridge(EesDataSource dataSource, IBusinessMembershipBridge memberships,
        IBusinessProjectDirectory projects, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new(dataSource);
        _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        _memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        _projects = projects ?? throw new ArgumentNullException(nameof(projects));
        _clock = clock ?? TimeProvider.System;
    }

    public Task<HrTask> CreateTaskAsync(string userId, Guid tenantId, Guid organizationId,
        HrTaskInput input, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.time.write",
            session => session.CreateTaskAsync(input, ct), ct);

    public Task<TimeEntry> StartTimerAsync(string userId, Guid tenantId, Guid organizationId,
        Guid? projectId = null, Guid? taskId = null, string description = "",
        CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.time.write", session =>
            TimeTrackingService.Create(session, session, _clock).StartAsync(
                session.Actor, session.EmployeeId, projectId, taskId, description, ct), ct);

    public Task<TimeEntry> StopTimerAsync(string userId, Guid tenantId, Guid organizationId,
        Guid entryId, Guid version, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.time.stop", session =>
            TimeTrackingService.Create(session, session, _clock).StopAsync(
                session.Actor, entryId, version, ct), ct);

    public Task<TimeEntry> RecordTimeAsync(string userId, Guid tenantId, Guid organizationId,
        ManualTimeInput input, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        return Run(userId, tenantId, organizationId, "hr.time.record-manual", session =>
            TimeTrackingService.Create(session, session, _clock).RecordAsync(session.Actor,
                session.EmployeeId, input.Start, input.End, input.ProjectId, input.TaskId,
                input.Description, ct), ct);
    }

    public Task<Timesheet> SubmitTimesheetAsync(string userId, Guid tenantId, Guid organizationId,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.timesheet.submit", session =>
            TimeTrackingService.Create(session, session, _clock).SubmitAsync(
                session.Actor, session.EmployeeId, start, end, ct), ct);

    public Task<TimeEntry> CorrectTimeAsync(string userId, Guid tenantId, Guid organizationId,
        Guid entryId, Guid version, DateTimeOffset start, DateTimeOffset end, string reason,
        CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.time.correct", session =>
            TimeTrackingService.Create(session, session, _clock).CorrectAsync(
                session.Actor, entryId, version, start, end, reason, ct), ct);

    public Task<Timesheet> ReviewTimesheetAsync(string userId, Guid tenantId, Guid organizationId,
        Guid timesheetId, Guid version, bool approve, string reason,
        CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.timesheet.review", session =>
            TimeTrackingService.Create(session, session, _clock).ReviewAsync(
                session.Actor, timesheetId, version, approve, reason, ct), ct);

    public Task<TimeEntry> GetTimeEntryAsync(string userId, Guid tenantId, Guid organizationId,
        Guid entryId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.time.read",
            session => session.GetEntryAsync(entryId, ct), ct);

    public Task<Timesheet> GetTimesheetAsync(string userId, Guid tenantId, Guid organizationId,
        Guid timesheetId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "hr.time.read",
            session => session.GetSheetAsync(timesheetId, ct), ct);

    public async Task<TimeBillingOccurrence?> GetTimeEntryInTransactionAsync(DbTransaction transaction,
        Guid tenantId, Guid organizationId, Guid timeEntryId, CancellationToken ct = default)
    {
        var rows = await FindTimeEntriesInTransactionAsync(transaction, tenantId, organizationId,
            [timeEntryId], ct);
        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<IReadOnlyList<TimeBillingOccurrence>> FindTimeEntriesInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId,
        IReadOnlyList<Guid> timeEntryIds, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(timeEntryIds);
        if (transaction.Connection is null || tenantId == Guid.Empty || organizationId == Guid.Empty
            || timeEntryIds.Count is < 1 or > 201 || timeEntryIds.Any(id => id == Guid.Empty)
            || timeEntryIds.Distinct().Count() != timeEntryIds.Count)
            throw new InvalidDataException("Invalid time billing directory request.");
        var command = new CommandDefinition("""
            SELECT E.PAYLOAD AS EntryPayload, S.STATE AS TimesheetState
              FROM HR_TIME_ENTRY E
              JOIN HR_TIMESHEET S
                ON S.TENANT_ID=E.TENANT_ID AND S.ORGANIZATION_ID=E.ORGANIZATION_ID
               AND S.TIMESHEET_ID=E.TIMESHEET_ID
             WHERE E.TENANT_ID=@TenantId AND E.ORGANIZATION_ID=@OrganizationId
               AND E.TIME_ENTRY_ID IN @Ids
             ORDER BY E.TIME_ENTRY_ID
            """, new { TenantId = Text(tenantId), OrganizationId = Text(organizationId),
                Ids = timeEntryIds.Select(Text).ToArray() }, transaction,
            commandTimeout: _timeout, cancellationToken: ct);
        var rows = (await transaction.Connection.QueryAsync<BillingRow>(command)).ToArray();
        if (rows.Length > timeEntryIds.Count) throw new InvalidDataException("Duplicate time billing rows.");
        var expected = timeEntryIds.ToHashSet();
        var expectedScope = new BusinessScope("NexaOne.MES", Text(tenantId), Text(organizationId));
        var result = new TimeBillingOccurrence[rows.Length];
        for (var index = 0; index < rows.Length; index++)
        {
            var entry = Deserialize<TimeEntry>(rows[index].EntryPayload);
            if (!expected.Remove(entry.Id) || entry.Scope != expectedScope || entry.End is null
                || entry.EmployeeId == Guid.Empty || entry.Version == Guid.Empty
                || entry.Start == default || entry.Start.Offset != TimeSpan.Zero
                || entry.End.Value.Offset != TimeSpan.Zero || entry.End <= entry.Start
                || entry.ProjectId == Guid.Empty || entry.WorkItemId == Guid.Empty
                || !entry.TimesheetId.HasValue || entry.TimesheetId == Guid.Empty
                || !ValidPayloadText(entry.Description, 4000)
                || !Enum.IsDefined((TimesheetState)rows[index].TimesheetState))
                throw new InvalidDataException("Time billing storage is invalid.");
            result[index] = new(entry.Id, entry.EmployeeId,
                DateOnly.FromDateTime(entry.End.Value.UtcDateTime),
                checked((entry.End.Value - entry.Start).Ticks), entry.Description,
                entry.ProjectId, entry.WorkItemId,
                rows[index].TimesheetState == (int)TimesheetState.Approved);
        }
        return Array.AsReadOnly(result);
    }

    private Task<T> Run<T>(string userId, Guid tenantId, Guid organizationId, string permission,
        Func<Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidUser(userId) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _projects,
                _clock);
            try
            {
                await session.AuthorizeAsync(userId, permission, ct);
                var result = await action(session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private static bool ValidUser(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 50 && value == value.Trim() && !value.Any(char.IsControl);
    private static bool ValidPayloadText(string? value, int maximum)
    {
        if (value is null || value.Length > maximum) return false;
        for (var index = 0; index < value.Length; index++)
            if (char.IsSurrogate(value[index])
                && (!char.IsHighSurrogate(value[index]) || ++index >= value.Length
                    || !char.IsLowSurrogate(value[index])))
                return false;
        return true;
    }
    private static string Text(Guid value) => value.ToString("D");
    private static Guid Id(string value) => Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty && Text(id) == value ? id
        : throw new InvalidDataException("HR storage contains an invalid identity.");
    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Json);
    private static T Deserialize<T>(string value) => JsonSerializer.Deserialize<T>(value, Json)
        ?? throw new InvalidDataException("HR storage contains an invalid payload.");
    private static BusinessException Failure(string code) => new(code);

    private sealed class BillingRow
    {
        public string EntryPayload { get; set; } = "";
        public int TimesheetState { get; set; }
    }

    private sealed class Session(DbConnection connection, DbTransaction transaction, int? timeout,
        BusinessScope scope, IBusinessMembershipBridge memberships, IBusinessProjectDirectory projects,
        TimeProvider clock)
        : IAtomicBusinessStore<ITimeTrackingTransaction>, ITimeTrackingTransaction, IBusinessAuthorizer
    {
        private bool _open = true;
        private int _invoked;
        private string[] _grants = [];
        public BusinessScope Scope { get; } = scope;
        internal BusinessActor Actor { get; private set; } = null!;
        internal Guid EmployeeId { get; private set; }
        internal void Close() => _open = false;

        internal async Task AuthorizeAsync(string userId, string permission, CancellationToken ct)
        {
            BusinessMembership? member;
            try
            {
                member = await memberships.GetAccessInTransactionAsync(transaction, userId,
                    Id(Scope.TenantId), Id(Scope.OrganizationId), ct);
            }
            catch (InvalidDataException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
            if (member is null || !member.Permissions.Contains(permission, StringComparer.Ordinal))
                throw Failure("BUSINESS_ACCESS_DENIED");
            EmployeeId = member.BusinessUserId;
            Actor = new(Text(EmployeeId), Scope);
            _grants = member.Permissions.ToArray();
        }

        public Task<bool> IsAllowedAsync(BusinessActor actor, string permission, string resourceType,
            Guid? resourceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(_open && actor == Actor
                && _grants.Contains(permission, StringComparer.Ordinal));
        }

        public async Task<T> ExecuteAsync<T>(BusinessScope requestedScope,
            Func<ITimeTrackingTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            if (!_open || requestedScope != Scope) throw Failure("STORAGE_SCOPE_VIOLATION");
            if (Interlocked.Exchange(ref _invoked, 1) != 0)
                throw Failure("TRANSACTION_REPLAY_NOT_ALLOWED");
            ct.ThrowIfCancellationRequested();
            var result = await work(this, ct);
            ct.ThrowIfCancellationRequested();
            return result;
        }

        public async Task<HrTask> CreateTaskAsync(HrTaskInput input, CancellationToken ct)
        {
            if (input is null || !ValidName(input.Name) || input.ProjectId == Guid.Empty)
                throw Failure("INVALID_BUSINESS_INPUT");
            if (input.ProjectId.HasValue && !await ProjectExists(input.ProjectId.Value, ct))
                throw Failure("HR_REFERENCE_NOT_FOUND");
            var task = new HrTask(Guid.NewGuid(), Guid.NewGuid(), input.Name,
                input.ProjectId, Active: true);
            await Write("""
                INSERT INTO HR_TASK
                    (TENANT_ID,ORGANIZATION_ID,TASK_ID,VERSION,NAME,PROJECT_ID,IS_ACTIVE,CREATED_BY)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Name,@ProjectId,@Active,@CreatedBy)
                """, new { Id = Text(task.Id), Version = Text(task.Version), task.Name,
                    ProjectId = task.ProjectId.HasValue ? Text(task.ProjectId.Value) : null,
                    Active = task.Active, CreatedBy = Actor.UserId }, ct);
            await AppendAuditAsync(new(Guid.NewGuid(), Actor, "hr-task", task.Id, "created",
                null, task.Version, clock.GetUtcNow()), ct);
            return task;
        }

        public async Task<bool> ReferenceExistsAsync(HrReferenceKind kind, Guid id,
            CancellationToken ct)
        {
            if (kind == HrReferenceKind.Project) return await ProjectExists(id, ct);
            if (kind != HrReferenceKind.WorkItem) return false;
            return await Scalar<long>("SELECT COUNT(*) FROM HR_TASK WHERE "
                + ScopeWhere + " AND TASK_ID=@Id AND IS_ACTIVE=1", new { Id = Text(id) }, ct) == 1;
        }

        public async Task<Employee?> FindEmployeeAsync(Guid id, CancellationToken ct)
        {
            var member = await memberships.GetActiveMemberInTransactionAsync(transaction,
                Id(Scope.TenantId), Id(Scope.OrganizationId), id, ct);
            return member is null ? null : new(id, Scope, id,
                new(member.UserId, member.UserId, new DateOnly(1970, 1, 1)), EmployeeState.Active);
        }

        public Task<TimeEntry?> FindTimeEntryAsync(Guid id, CancellationToken ct)
            => Payload<TimeEntry>("HR_TIME_ENTRY", "TIME_ENTRY_ID", id, ct);

        public async Task<TimeEntry?> FindRunningTimeAsync(Guid employeeId, CancellationToken ct)
        {
            var payload = await Scalar<string?>("SELECT PAYLOAD FROM HR_TIME_ENTRY WHERE "
                + ScopeWhere + " AND EMPLOYEE_ID=@Employee AND END_TICKS IS NULL",
                new { Employee = Text(employeeId) }, ct);
            return payload is null ? null : Deserialize<TimeEntry>(payload);
        }

        public async Task<bool> HasTimeOverlapAsync(Guid employeeId, DateTimeOffset start,
            DateTimeOffset? end, Guid? exceptId, CancellationToken ct)
            => await Scalar<long>("SELECT COUNT(*) FROM HR_TIME_ENTRY WHERE " + ScopeWhere
                + " AND EMPLOYEE_ID=@Employee AND TIME_ENTRY_ID<>COALESCE(@Except,'')"
                + " AND START_TICKS<@End AND COALESCE(END_TICKS,9223372036854775807)>@Start",
                new { Employee = Text(employeeId), Except = exceptId.HasValue ? Text(exceptId.Value) : null,
                    Start = start.UtcTicks, End = end?.UtcTicks ?? long.MaxValue }, ct) > 0;

        public async Task<Guid?> FindWorkItemProjectAsync(Guid workItemId, CancellationToken ct)
        {
            var value = await Scalar<string?>("SELECT PROJECT_ID FROM HR_TASK WHERE " + ScopeWhere
                + " AND TASK_ID=@Id AND IS_ACTIVE=1", new { Id = Text(workItemId) }, ct);
            return value is null ? null : Id(value);
        }

        public Task SaveTimeEntryAsync(TimeEntry value, Guid? expectedVersion, CancellationToken ct)
        {
            ValidateEntry(value);
            if (expectedVersion == Guid.Empty) throw Failure("STORAGE_CONTRACT_VIOLATION");
            return expectedVersion.HasValue
                ? UpdateEntry(value, expectedVersion.Value, ct)
                : InsertEntry(value, ct);
        }

        public async Task<IReadOnlyList<TimeEntry>> ReadTimeEntriesAsync(Guid employeeId,
            DateTimeOffset start, DateTimeOffset end, int maximum, CancellationToken ct)
        {
            var rows = await Rows<PayloadRow>("SELECT PAYLOAD AS Payload FROM HR_TIME_ENTRY WHERE "
                + ScopeWhere + " AND EMPLOYEE_ID=@Employee AND START_TICKS>=@Start"
                + " AND END_TICKS<=@End ORDER BY START_TICKS,TIME_ENTRY_ID",
                new { Employee = Text(employeeId), Start = start.UtcTicks, End = end.UtcTicks }, ct);
            if (rows.Length > maximum) throw Failure("TIME_RESULT_TOO_LARGE");
            return Array.AsReadOnly(rows.Select(row => Deserialize<TimeEntry>(row.Payload)).ToArray());
        }

        public async Task<bool> HasTimesheetOverlapAsync(Guid employeeId, DateTimeOffset start,
            DateTimeOffset end, CancellationToken ct)
            => await Scalar<long>("SELECT COUNT(*) FROM HR_TIMESHEET WHERE " + ScopeWhere
                + " AND EMPLOYEE_ID=@Employee AND STATE<>@Rejected"
                + " AND START_TICKS<@End AND END_TICKS>@Start",
                new { Employee = Text(employeeId), Rejected = (int)TimesheetState.Rejected,
                    Start = start.UtcTicks, End = end.UtcTicks }, ct) > 0;

        public Task<Timesheet?> FindTimesheetAsync(Guid id, CancellationToken ct)
            => Payload<Timesheet>("HR_TIMESHEET", "TIMESHEET_ID", id, ct);

        public Task SaveTimesheetAsync(Timesheet value, Guid? expectedVersion, CancellationToken ct)
        {
            ValidateSheet(value);
            if (expectedVersion == Guid.Empty) throw Failure("STORAGE_CONTRACT_VIOLATION");
            return expectedVersion.HasValue
                ? UpdateSheet(value, expectedVersion.Value, ct)
                : InsertSheet(value, ct);
        }

        public Task AppendAuditAsync(BusinessAudit audit, CancellationToken ct)
        {
            if (audit is null || audit.Actor != Actor || audit.Actor.Scope != Scope
                || audit.Id == Guid.Empty || audit.ResourceId == Guid.Empty || audit.Version == Guid.Empty
                || audit.PreviousVersion == Guid.Empty || audit.At == default || audit.At.Offset != TimeSpan.Zero
                || !ValidName(audit.ResourceType, 64) || !ValidName(audit.Operation, 64))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
            return Write("""
                INSERT INTO HR_TIME_AUDIT
                    (TENANT_ID,ORGANIZATION_ID,AUDIT_ID,USER_ID,RESOURCE_TYPE,RESOURCE_ID,
                     OPERATION,PREVIOUS_VERSION,VERSION,AT_TICKS)
                VALUES (@TenantId,@OrganizationId,@Id,@UserId,@ResourceType,@ResourceId,
                        @Operation,@Previous,@Version,@At)
                """, new { Id = Text(audit.Id), UserId = audit.Actor.UserId, audit.ResourceType,
                    ResourceId = Text(audit.ResourceId), audit.Operation,
                    Previous = audit.PreviousVersion.HasValue ? Text(audit.PreviousVersion.Value) : null,
                    Version = Text(audit.Version), At = audit.At.UtcTicks }, ct);
        }

        internal async Task<TimeEntry> GetEntryAsync(Guid id, CancellationToken ct)
        {
            if (id == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var value = await FindTimeEntryAsync(id, ct) ?? throw Failure("TIME_ENTRY_NOT_FOUND");
            ValidateEntry(value);
            return value;
        }

        internal async Task<Timesheet> GetSheetAsync(Guid id, CancellationToken ct)
        {
            if (id == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            var value = await FindTimesheetAsync(id, ct) ?? throw Failure("TIMESHEET_NOT_FOUND");
            ValidateSheet(value);
            return value;
        }

        private Task<bool> ProjectExists(Guid id, CancellationToken ct)
            => projects.ProjectExistsInTransactionAsync(transaction, Id(Scope.TenantId),
                Id(Scope.OrganizationId), id, ct);

        private Task InsertEntry(TimeEntry value, CancellationToken ct)
            => Write("""
                INSERT INTO HR_TIME_ENTRY
                    (TENANT_ID,ORGANIZATION_ID,TIME_ENTRY_ID,VERSION,EMPLOYEE_ID,START_TICKS,
                     END_TICKS,PROJECT_ID,TASK_ID,TIMESHEET_ID,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Employee,@Start,@End,
                        @Project,@Task,@Timesheet,@Payload)
                """, EntryValues(value, previous: null), ct);

        private Task UpdateEntry(TimeEntry value, Guid previous, CancellationToken ct)
            => Write("""
                UPDATE HR_TIME_ENTRY SET VERSION=@Version,START_TICKS=@Start,END_TICKS=@End,
                       PROJECT_ID=@Project,TASK_ID=@Task,TIMESHEET_ID=@Timesheet,PAYLOAD=@Payload
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                   AND TIME_ENTRY_ID=@Id AND VERSION=@Previous
                """, EntryValues(value, previous), ct);

        private static object EntryValues(TimeEntry value, Guid? previous) => new
        {
            Id = Text(value.Id), Version = Text(value.Version), Employee = Text(value.EmployeeId),
            Start = value.Start.UtcTicks, End = value.End?.UtcTicks,
            Project = value.ProjectId.HasValue ? Text(value.ProjectId.Value) : null,
            Task = value.WorkItemId.HasValue ? Text(value.WorkItemId.Value) : null,
            Timesheet = value.TimesheetId.HasValue ? Text(value.TimesheetId.Value) : null,
            Payload = Serialize(value), Previous = previous.HasValue ? Text(previous.Value) : null
        };

        private Task InsertSheet(Timesheet value, CancellationToken ct)
            => Write("""
                INSERT INTO HR_TIMESHEET
                    (TENANT_ID,ORGANIZATION_ID,TIMESHEET_ID,VERSION,EMPLOYEE_ID,START_TICKS,
                     END_TICKS,STATE,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Employee,@Start,@End,@State,@Payload)
                """, SheetValues(value, previous: null), ct);

        private Task UpdateSheet(Timesheet value, Guid previous, CancellationToken ct)
            => Write("""
                UPDATE HR_TIMESHEET SET VERSION=@Version,STATE=@State,PAYLOAD=@Payload
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                   AND TIMESHEET_ID=@Id AND VERSION=@Previous
                """, SheetValues(value, previous), ct);

        private static object SheetValues(Timesheet value, Guid? previous) => new
        {
            Id = Text(value.Id), Version = Text(value.Version), Employee = Text(value.EmployeeId),
            Start = value.Start.UtcTicks, End = value.End.UtcTicks, State = (int)value.State,
            Payload = Serialize(value), Previous = previous.HasValue ? Text(previous.Value) : null
        };

        private async Task<T?> Payload<T>(string table, string key, Guid id, CancellationToken ct)
            where T : class
        {
            var payload = await Scalar<string?>($"SELECT PAYLOAD FROM {table} WHERE {ScopeWhere} AND {key}=@Id",
                new { Id = Text(id) }, ct);
            return payload is null ? null : Deserialize<T>(payload);
        }

        private CommandDefinition Command(string sql, object? values, CancellationToken ct)
        {
            if (!_open) throw new ObjectDisposedException(nameof(Session));
            ct.ThrowIfCancellationRequested();
            var parameters = new DynamicParameters(values);
            parameters.Add("TenantId", Scope.TenantId);
            parameters.Add("OrganizationId", Scope.OrganizationId);
            return new(sql, parameters, transaction, commandTimeout: timeout, cancellationToken: ct);
        }

        private async Task<T> Scalar<T>(string sql, object? values, CancellationToken ct)
            => (await connection.ExecuteScalarAsync<T>(Command(sql, values, ct)))!;
        private async Task<T[]> Rows<T>(string sql, object? values, CancellationToken ct)
            => (await connection.QueryAsync<T>(Command(sql, values, ct))).ToArray();
        private async Task Write(string sql, object values, CancellationToken ct)
        {
            if (await connection.ExecuteAsync(Command(sql, values, ct)) != 1)
                throw new DBConcurrencyException("HR write did not affect exactly one row.");
        }
        private static bool ValidName(string? value, int maximum = 255) => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maximum && value == value.Trim() && !value.Any(char.IsControl)
            && WellFormed(value);

        private static bool WellFormed(string value)
        {
            for (var index = 0; index < value.Length; index++)
                if (char.IsSurrogate(value[index])
                    && (!char.IsHighSurrogate(value[index]) || ++index >= value.Length
                        || !char.IsLowSurrogate(value[index])))
                    return false;
            return true;
        }

        private void ValidateEntry(TimeEntry value)
        {
            if (value is null || value.Scope != Scope || value.Id == Guid.Empty
                || value.Version == Guid.Empty || value.EmployeeId == Guid.Empty
                || value.Start == default || value.Start.Offset != TimeSpan.Zero
                || value.End.HasValue && (value.End.Value.Offset != TimeSpan.Zero
                    || value.End.Value <= value.Start)
                || value.ProjectId == Guid.Empty || value.WorkItemId == Guid.Empty
                || value.TimesheetId == Guid.Empty || value.Description is null
                || value.Description.Length > 4000 || !WellFormed(value.Description))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        private void ValidateSheet(Timesheet value)
        {
            if (value is null || value.Scope != Scope || value.Id == Guid.Empty
                || value.Version == Guid.Empty || value.EmployeeId == Guid.Empty
                || value.Start == default || value.Start.Offset != TimeSpan.Zero
                || value.End.Offset != TimeSpan.Zero || value.End <= value.Start
                || value.DurationTicks <= 0 || !Enum.IsDefined(value.State)
                || value.EntryIds is null || value.EntryIds.Count is < 1 or > 5000
                || value.EntryIds.Any(id => id == Guid.Empty)
                || value.EntryIds.Distinct().Count() != value.EntryIds.Count
                || !ValidName(value.SubmittedBy, 128)
                || value.State == TimesheetState.Submitted && value.DecisionReason is not null
                || value.State != TimesheetState.Submitted
                    && !ValidName(value.DecisionReason, 2000))
                throw Failure("STORAGE_CONTRACT_VIOLATION");
        }

        private sealed class PayloadRow { public string Payload { get; set; } = ""; }
    }
}
