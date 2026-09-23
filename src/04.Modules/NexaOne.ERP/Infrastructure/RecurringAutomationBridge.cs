using System.Data;
using System.Data.Common;
using Dapper;
using NexaFramework.Service;
using NexaFramework.Service.Erp;
using NexaOne.Common;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge : IRecurringAutomationBridge
{
    private const string ServiceUserPrefix = "svc.erp.";

    public Task<Result<RecurringServicePrincipal>> GetPrincipalAsync(
        string administratorId, string principalId, CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<RecurringServicePrincipal>>(async (connection, transaction) =>
        {
            await _memberships.RequireAdministratorInTransactionAsync(transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId)) return InvalidPrincipal();
            var row = await ReadPrincipal(connection, transaction, principalId, ct);
            return row is null
                ? Result.Failure<RecurringServicePrincipal>(Error.NotFound("Recurring service principal was not found."))
                : Result.Success(ToPrincipal(row));
        }, IsolationLevel.Serializable, ct);

    public Task<Result<RecurringServicePrincipal>> SavePrincipalAsync(
        string administratorId, string principalId, RecurringServicePrincipalChange change,
        CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<RecurringServicePrincipal>>(async (connection, transaction) =>
        {
            var administrator = await _memberships.RequireAdministratorInTransactionAsync(
                transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId) || change is null || change.ExpectedVersion < 0
                || change.ExpectedVersion == long.MaxValue || !ValidText(change.Name, 200))
                return InvalidPrincipal();

            var current = await ReadPrincipal(connection, transaction, principalId, ct);
            if ((current?.Version ?? 0) != change.ExpectedVersion)
                return Result.Failure<RecurringServicePrincipal>(
                    Error.Conflict("Service principal version changed; read before retrying."));
            if (current is not null) _ = ToPrincipal(current);

            var serviceActor = await _memberships.EnsureServiceActorInTransactionAsync(
                transaction, administrator, ServiceUserPrefix + principalId, change.Name.Trim(), ct);
            var revision = change.ExpectedVersion + 1;
            var now = _clock.GetUtcNow().UtcDateTime;
            var auditActorId = serviceActor.BusinessActorId;
            if (current is not null && auditActorId != Id(current.AuditActorId))
                throw new InvalidDataException("Recurring service principal audit identity changed.");
            var values = new
            {
                PrincipalId = principalId,
                AuditActorId = Text(auditActorId),
                Name = change.Name.Trim(),
                change.IsActive,
                Version = revision,
                change.ExpectedVersion,
                Administrator = administrator,
                Now = now,
            };
            var affected = await connection.ExecuteAsync(new CommandDefinition(current is null ? """
                INSERT INTO ERP_RECURRING_SERVICE_PRINCIPAL
                    (PRINCIPAL_ID, AUDIT_ACTOR_ID, NAME, IS_ACTIVE, PRINCIPAL_VERSION, UPDATED_BY, UPDATED_AT)
                VALUES (@PrincipalId, @AuditActorId, @Name, @IsActive, @Version, @Administrator, @Now)
                """ : """
                UPDATE ERP_RECURRING_SERVICE_PRINCIPAL
                   SET NAME=@Name, IS_ACTIVE=@IsActive, PRINCIPAL_VERSION=@Version,
                       UPDATED_BY=@Administrator, UPDATED_AT=@Now
                 WHERE PRINCIPAL_ID=@PrincipalId AND PRINCIPAL_VERSION=@ExpectedVersion
                """, values, transaction, _timeout, cancellationToken: ct));
            if (affected != 1) throw new DBConcurrencyException("Service principal write did not affect exactly one row.");
            await InsertPrincipalAudit(connection, transaction, values, ct);
            return Result.Success(new RecurringServicePrincipal(
                principalId, auditActorId, change.Name.Trim(), change.IsActive, revision));
        }, IsolationLevel.Serializable, ct);

    public Task<Result<RecurringServicePrincipalScope>> GetScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<RecurringServicePrincipalScope>>(async (connection, transaction) =>
        {
            await _memberships.RequireAdministratorInTransactionAsync(transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId) || tenantId == Guid.Empty || organizationId == Guid.Empty)
                return InvalidScope();
            var row = await ReadPrincipalScope(connection, transaction, principalId, tenantId, organizationId, ct);
            return row is null
                ? Result.Failure<RecurringServicePrincipalScope>(Error.NotFound("Recurring service principal scope was not found."))
                : Result.Success(ToScope(row));
        }, IsolationLevel.Serializable, ct);

    public Task<Result<RecurringServicePrincipalScope>> SaveScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        RecurringServicePrincipalScopeChange change, CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<RecurringServicePrincipalScope>>(async (connection, transaction) =>
        {
            var administrator = await _memberships.RequireAdministratorInTransactionAsync(
                transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId) || tenantId == Guid.Empty || organizationId == Guid.Empty
                || change is null || change.ExpectedVersion < 0 || change.ExpectedVersion == long.MaxValue)
                return InvalidScope();
            if (await ReadPrincipal(connection, transaction, principalId, ct) is not { } principal)
                return Result.Failure<RecurringServicePrincipalScope>(Error.NotFound("Recurring service principal was not found."));
            _ = ToPrincipal(principal);

            var current = await ReadPrincipalScope(
                connection, transaction, principalId, tenantId, organizationId, ct);
            if ((current?.Version ?? 0) != change.ExpectedVersion)
                return Result.Failure<RecurringServicePrincipalScope>(
                    Error.Conflict("Service principal scope version changed; read before retrying."));
            if (current is not null) _ = ToScope(current);

            var revision = change.ExpectedVersion + 1;
            var now = _clock.GetUtcNow().UtcDateTime;
            var values = new
            {
                PrincipalId = principalId,
                TenantId = Text(tenantId),
                OrganizationId = Text(organizationId),
                change.IsActive,
                Version = revision,
                change.ExpectedVersion,
                Administrator = administrator,
                Now = now,
            };
            var affected = await connection.ExecuteAsync(new CommandDefinition(current is null ? """
                INSERT INTO ERP_RECURRING_SERVICE_SCOPE
                    (PRINCIPAL_ID, TENANT_ID, ORGANIZATION_ID, IS_ACTIVE, SCOPE_VERSION, UPDATED_BY, UPDATED_AT)
                VALUES (@PrincipalId, @TenantId, @OrganizationId, @IsActive, @Version, @Administrator, @Now)
                """ : """
                UPDATE ERP_RECURRING_SERVICE_SCOPE
                   SET IS_ACTIVE=@IsActive, SCOPE_VERSION=@Version, UPDATED_BY=@Administrator, UPDATED_AT=@Now
                 WHERE PRINCIPAL_ID=@PrincipalId AND TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                   AND SCOPE_VERSION=@ExpectedVersion
                """, values, transaction, _timeout, cancellationToken: ct));
            if (affected != 1) throw new DBConcurrencyException("Service principal scope write did not affect exactly one row.");
            await InsertScopeAudit(connection, transaction, values, ct);
            return Result.Success(new RecurringServicePrincipalScope(
                principalId, tenantId, organizationId, change.IsActive, revision));
        }, IsolationLevel.Serializable, ct);

    public Task<BusinessPage<RecurringServicePrincipalScope>> ListActiveScopesAsync(
        string principalId, int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!ValidPrincipalId(principalId)) throw Failure("BUSINESS_ACCESS_DENIED");
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var principal = await ReadPrincipal(connection, transaction, principalId, ct);
            if (principal is null || !ToPrincipal(principal).IsActive) throw Failure("BUSINESS_ACCESS_DENIED");
            var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT COUNT(*) FROM ERP_RECURRING_SERVICE_SCOPE
                 WHERE PRINCIPAL_ID=@PrincipalId AND IS_ACTIVE=1
                """, new { PrincipalId = principalId }, transaction, _timeout, cancellationToken: ct));
            var rows = (await connection.QueryAsync<PrincipalScopeRow>(new CommandDefinition("""
                SELECT PRINCIPAL_ID AS PrincipalId, TENANT_ID AS TenantId, ORGANIZATION_ID AS OrganizationId,
                       IS_ACTIVE AS IsActive, SCOPE_VERSION AS Version
                  FROM (SELECT PRINCIPAL_ID, TENANT_ID, ORGANIZATION_ID, IS_ACTIVE, SCOPE_VERSION,
                               ROW_NUMBER() OVER (ORDER BY TENANT_ID, ORGANIZATION_ID) AS RowNumber
                          FROM ERP_RECURRING_SERVICE_SCOPE
                         WHERE PRINCIPAL_ID=@PrincipalId AND IS_ACTIVE=1) AS page
                 WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber
                """, new { PrincipalId = principalId, Offset = offset, EndRow = (long)offset + limit },
                transaction, _timeout, cancellationToken: ct))).Select(ToScope).ToArray();
            return new BusinessPage<RecurringServicePrincipalScope>(Array.AsReadOnly(rows), count);
        }, IsolationLevel.Serializable, ct);
    }

    public Task<BusinessPage<RecurringRule>> ListDueRulesAsync(
        string principalId, Guid tenantId, Guid organizationId, DateOnly localDate,
        int offset = 0, int limit = 50, CancellationToken ct = default)
    {
        if (!ValidPrincipalId(principalId) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        if (localDate == default || offset < 0 || limit is < 1 or > 100)
            throw Failure("INVALID_BUSINESS_INPUT");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock);
            try
            {
                await session.AuthorizePrincipal(principalId, ct);
                var month = new DateOnly(localDate.Year, localDate.Month, 1);
                var items = new List<RecurringRule>(limit);
                long total = 0;
                var scanned = 0;
                while (true)
                {
                    var page = await session.QueryRecurringRulesAsync(new(Active: true, Offset: scanned, Limit: 100), ct);
                    foreach (var rule in page.Items)
                    {
                        var schedule = rule.Input.Schedule;
                        var dueDay = Math.Min(schedule.DayOfMonth, DateTime.DaysInMonth(localDate.Year, localDate.Month));
                        if (month < schedule.StartMonth || schedule.EndMonth.HasValue && month > schedule.EndMonth.Value
                            || localDate.Day < dueDay) continue;
                        if (total++ >= offset && items.Count < limit) items.Add(rule);
                    }
                    scanned += page.Items.Count;
                    if (scanned >= page.Total || page.Items.Count == 0) break;
                }
                return new BusinessPage<RecurringRule>(Array.AsReadOnly(items.ToArray()), total);
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    Task<RecurringExecution> IRecurringAutomationBridge.ExecuteOccurrenceAsync(
        string principalId, Guid tenantId, Guid organizationId, Guid ruleId, DateOnly month,
        CancellationToken ct)
    {
        if (!ValidPrincipalId(principalId) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock);
            try
            {
                await session.AuthorizePrincipal(principalId, ct);
                var result = await new RecurringService(session, session, _clock)
                    .ExecuteOccurrenceAsync(session.Actor, ruleId, month, ct);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private static bool ValidPrincipalId(string? value)
    {
        if (value is null || value.Length is < 3 or > 40 || value != value.Trim()) return false;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character is >= 'a' and <= 'z' || character is >= '0' and <= '9'
                || index > 0 && character is '.' or '_' or '-') continue;
            return false;
        }
        return true;
    }

    private static Result<RecurringServicePrincipal> InvalidPrincipal()
        => Result.Failure<RecurringServicePrincipal>(Error.Validation("Invalid recurring service principal."));
    private static Result<RecurringServicePrincipalScope> InvalidScope()
        => Result.Failure<RecurringServicePrincipalScope>(Error.Validation("Invalid recurring service principal scope."));

    private Task<PrincipalRow?> ReadPrincipal(DbConnection connection, DbTransaction transaction,
        string principalId, CancellationToken ct)
        => connection.QuerySingleOrDefaultAsync<PrincipalRow>(new CommandDefinition("""
            SELECT PRINCIPAL_ID AS PrincipalId, AUDIT_ACTOR_ID AS AuditActorId,
                   NAME AS Name, IS_ACTIVE AS IsActive,
                   PRINCIPAL_VERSION AS Version
              FROM ERP_RECURRING_SERVICE_PRINCIPAL WHERE PRINCIPAL_ID=@PrincipalId
            """, new { PrincipalId = principalId }, transaction, _timeout, cancellationToken: ct));

    private Task<PrincipalScopeRow?> ReadPrincipalScope(DbConnection connection, DbTransaction transaction,
        string principalId, Guid tenantId, Guid organizationId, CancellationToken ct)
        => connection.QuerySingleOrDefaultAsync<PrincipalScopeRow>(new CommandDefinition("""
            SELECT PRINCIPAL_ID AS PrincipalId, TENANT_ID AS TenantId, ORGANIZATION_ID AS OrganizationId,
                   IS_ACTIVE AS IsActive, SCOPE_VERSION AS Version
              FROM ERP_RECURRING_SERVICE_SCOPE
             WHERE PRINCIPAL_ID=@PrincipalId AND TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
            """, new { PrincipalId = principalId, TenantId = Text(tenantId), OrganizationId = Text(organizationId) },
            transaction, _timeout, cancellationToken: ct));

    private Task InsertPrincipalAudit(DbConnection connection, DbTransaction transaction, object values,
        CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ERP_RECURRING_SERVICE_PRINCIPAL_AUDIT
                (PRINCIPAL_ID, PRINCIPAL_VERSION, NAME, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
            VALUES (@PrincipalId, @Version, @Name, @IsActive, @Administrator, @Now)
            """, values, transaction, _timeout, cancellationToken: ct));

    private Task InsertScopeAudit(DbConnection connection, DbTransaction transaction, object values,
        CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition("""
            INSERT INTO ERP_RECURRING_SERVICE_SCOPE_AUDIT
                (PRINCIPAL_ID, TENANT_ID, ORGANIZATION_ID, SCOPE_VERSION, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
            VALUES (@PrincipalId, @TenantId, @OrganizationId, @Version, @IsActive, @Administrator, @Now)
            """, values, transaction, _timeout, cancellationToken: ct));

    private static RecurringServicePrincipal ToPrincipal(PrincipalRow row)
    {
        if (!ValidPrincipalId(row.PrincipalId) || !ValidText(row.Name, 200) || row.Version <= 0)
            throw new InvalidDataException("Recurring service principal storage is invalid.");
        return new(row.PrincipalId, Id(row.AuditActorId), row.Name, row.IsActive, row.Version);
    }

    private static RecurringServicePrincipalScope ToScope(PrincipalScopeRow row)
    {
        if (!ValidPrincipalId(row.PrincipalId) || row.Version <= 0)
            throw new InvalidDataException("Recurring service principal scope storage is invalid.");
        return new(row.PrincipalId, Id(row.TenantId), Id(row.OrganizationId), row.IsActive, row.Version);
    }

    private sealed class PrincipalRow
    {
        public string PrincipalId { get; set; } = "";
        public string AuditActorId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
        public long Version { get; set; }
    }

    private sealed class PrincipalScopeRow
    {
        public string PrincipalId { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string OrganizationId { get; set; } = "";
        public bool IsActive { get; set; }
        public long Version { get; set; }
    }

    private sealed partial class Session
    {
        internal async Task AuthorizePrincipal(string principalId, CancellationToken ct)
        {
            var stored = await Row<PrincipalAuthorityRow>("""
                SELECT p.PRINCIPAL_ID AS PrincipalId, p.AUDIT_ACTOR_ID AS AuditActorId
                  FROM ERP_RECURRING_SERVICE_PRINCIPAL p
                  JOIN ERP_RECURRING_SERVICE_SCOPE s ON s.PRINCIPAL_ID=p.PRINCIPAL_ID
                 WHERE p.PRINCIPAL_ID=@PrincipalId AND p.IS_ACTIVE=1 AND s.IS_ACTIVE=1
                   AND s.TENANT_ID=@TenantId AND s.ORGANIZATION_ID=@OrganizationId
                """, new { PrincipalId = principalId }, ct);
            if (stored is null || !string.Equals(stored.PrincipalId, principalId, StringComparison.Ordinal))
                throw Failure("BUSINESS_ACCESS_DENIED");
            Actor = new(Text(Id(stored.AuditActorId)), Scope);
            _grants = ["recurring.execute"];
        }
    }

    private sealed class PrincipalAuthorityRow
    {
        public string PrincipalId { get; set; } = "";
        public string AuditActorId { get; set; } = "";
    }
}
