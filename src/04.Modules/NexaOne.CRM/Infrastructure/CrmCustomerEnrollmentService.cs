using System.Data;
using System.Data.Common;
using Dapper;
using NexaDB.Data.Abstractions.Models;
using NexaFramework.Service;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.CRM.Infrastructure;

/// <summary>Owns the stable organization-scoped identity between MDM customers and CRM contacts.</summary>
internal sealed class CrmCustomerEnrollmentService
{
    private readonly ServiceObjectProcessor _processor;
    private readonly IBusinessMembershipBridge _memberships;
    private readonly IBusinessMasterDirectory _masterDirectory;
    private readonly int? _timeout;
    private readonly string _pageSql;
    private readonly string _contains;
    private readonly TimeProvider _time;

    public CrmCustomerEnrollmentService(EesDataSource dataSource, IBusinessMembershipBridge memberships,
        IBusinessMasterDirectory masterDirectory, TimeProvider? time = null)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _memberships = memberships;
        _masterDirectory = masterDirectory;
        _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        _pageSql = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer
            ? " OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY"
            : " LIMIT @Limit OFFSET @Offset";
        _contains = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer
            ? "CHARINDEX(@Text, {0}) > 0" : "INSTR({0}, @Text) > 0";
        _time = time ?? TimeProvider.System;
    }

    public Task<CrmCustomerEnrollment> EnrollAsync(BusinessActor actor, string customerId, CancellationToken ct)
    {
        RequireCustomerId(customerId);
        return Execute(actor, CrmPermissions.EnrollCustomer, async (connection, transaction, scope, token) =>
        {
            var existing = await FindByCustomer(connection, transaction, scope, customerId, token).ConfigureAwait(false);
            if (existing is not null) return existing.Value();

            var customer = await _masterDirectory.FindCustomerAsync(transaction, customerId, token).ConfigureAwait(false)
                ?? throw new BusinessException("MDM_CUSTOMER_NOT_FOUND");
            if (!customer.IsActive) throw new BusinessException("MDM_CUSTOMER_INACTIVE");
            if (!string.Equals(customer.CustomerId, customerId, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(customer.CustomerName) || customer.CustomerName.Length > 255)
                throw new BusinessException("CRM_ENROLLMENT_MASTER_INVALID");

            var enrollment = new CrmCustomerEnrollment(Guid.NewGuid(), Guid.NewGuid(), customer.CustomerId,
                customer.CustomerName, actor.UserId, _time.GetUtcNow());
            await ExecuteOne(connection, transaction, """
                INSERT INTO CRM_CUSTOMER_ENROLLMENT
                    (TENANT_ID,ORGANIZATION_ID,CONTACT_ID,VERSION,MDM_CUSTOMER_ID,CUSTOMER_NAME,
                     ENROLLED_BY,ENROLLED_AT_TICKS)
                VALUES (@TenantId,@OrganizationId,@ContactId,@Version,@CustomerId,@CustomerName,
                        @EnrolledBy,@EnrolledAt)
                """, Values(scope, enrollment), "CRM_ENROLLMENT_CONFLICT", token).ConfigureAwait(false);
            await Audit(connection, transaction, scope, actor.UserId, "customer.enrolled",
                enrollment.ContactId, null, enrollment.Version, enrollment.EnrolledAt, token).ConfigureAwait(false);
            return enrollment;
        }, ct);
    }

    public Task<CrmCustomerEnrollmentPage> ListAsync(BusinessActor actor, int offset, int limit,
        string? text, CancellationToken ct)
    {
        if (offset < 0 || limit is < 1 or > 100 || text?.Length > 256)
            throw new BusinessException("INVALID_CRM_ENROLLMENT_QUERY");
        return Execute<CrmCustomerEnrollmentPage>(actor, CrmPermissions.Read,
            async (connection, transaction, scope, token) =>
            {
                var where = " WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId";
                if (text is not null)
                    where += " AND (" + string.Format(_contains, "MDM_CUSTOMER_ID")
                        + " OR " + string.Format(_contains, "CUSTOMER_NAME") + ")";
                var values = new { scope.TenantId, scope.OrganizationId, Offset = offset, Limit = limit, Text = text };
                var total = await connection.ExecuteScalarAsync<long>(Command(
                    "SELECT COUNT(*) FROM CRM_CUSTOMER_ENROLLMENT" + where, values, transaction, token));
                var rows = await connection.QueryAsync<Row>(Command("""
                    SELECT CONTACT_ID AS ContactId, VERSION AS Version, MDM_CUSTOMER_ID AS CustomerId,
                           CUSTOMER_NAME AS CustomerName, ENROLLED_BY AS EnrolledBy,
                           ENROLLED_AT_TICKS AS EnrolledAt
                      FROM CRM_CUSTOMER_ENROLLMENT
                    """ + where + " ORDER BY MDM_CUSTOMER_ID,CONTACT_ID" + _pageSql,
                    values, transaction, token));
                return new(Array.AsReadOnly(rows.Select(row => row.Value()).ToArray()), total);
            }, ct);
    }

    public Task DeleteAsync(BusinessActor actor, Guid contactId, Guid version, CancellationToken ct)
    {
        if (contactId == Guid.Empty || version == Guid.Empty) throw new BusinessException("INVALID_CRM_ENROLLMENT");
        return Execute(actor, CrmPermissions.EnrollCustomer, async (connection, transaction, scope, token) =>
        {
            var values = new
            {
                scope.TenantId,
                scope.OrganizationId,
                ContactId = contactId.ToString("D"),
                Version = version.ToString("D"),
            };
            var current = await connection.QuerySingleOrDefaultAsync<Row>(Command("""
                SELECT CONTACT_ID AS ContactId, VERSION AS Version, MDM_CUSTOMER_ID AS CustomerId,
                       CUSTOMER_NAME AS CustomerName, ENROLLED_BY AS EnrolledBy,
                       ENROLLED_AT_TICKS AS EnrolledAt
                  FROM CRM_CUSTOMER_ENROLLMENT
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND CONTACT_ID=@ContactId
                """, values, transaction, token));
            if (current is null) throw new BusinessException("CRM_ENROLLMENT_NOT_FOUND");
            if (current.Value().Version != version) throw new BusinessException("CRM_ENROLLMENT_VERSION_CONFLICT");
            var references = await connection.ExecuteScalarAsync<long>(Command("""
                SELECT (SELECT COUNT(*) FROM CRM_DEAL
                         WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND CLIENT_ID=@ContactId)
                     + (SELECT COUNT(*) FROM CRM_PROJECT
                         WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND CUSTOMER_ID=@ContactId)
                """, values, transaction, token));
            if (references > 0) throw new BusinessException("CRM_ENROLLMENT_IS_REFERENCED");
            await ExecuteOne(connection, transaction, """
                DELETE FROM CRM_CUSTOMER_ENROLLMENT
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                   AND CONTACT_ID=@ContactId AND VERSION=@Version
                """, values, "CRM_ENROLLMENT_VERSION_CONFLICT", token).ConfigureAwait(false);
            await Audit(connection, transaction, scope, actor.UserId, "customer.unenrolled",
                contactId, version, null, _time.GetUtcNow(), token).ConfigureAwait(false);
        }, ct);
    }

    private async Task<T> Execute<T>(BusinessActor actor, string permission,
        Func<DbConnection, DbTransaction, Scope, CancellationToken, Task<T>> work, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(actor);
        ct.ThrowIfCancellationRequested();
        var scope = Scope.Parse(actor.Scope);
        try
        {
            return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
            {
                var access = await _memberships.GetAccessInTransactionAsync(transaction, actor.UserId,
                    scope.TenantGuid, scope.OrganizationGuid, ct).ConfigureAwait(false);
                if (access is null || !access.Permissions.Contains(permission, StringComparer.Ordinal))
                    throw new BusinessException("CRM_ACCESS_DENIED");
                var result = await work(connection, transaction, scope, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                return result;
            }, IsolationLevel.Serializable, ct).ConfigureAwait(false);
        }
        catch (BusinessException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw new BusinessStorageException(error); }
    }

    private async Task Execute(BusinessActor actor, string permission,
        Func<DbConnection, DbTransaction, Scope, CancellationToken, Task> work, CancellationToken ct)
        => await Execute(actor, permission, async (connection, transaction, scope, token) =>
        {
            await work(connection, transaction, scope, token).ConfigureAwait(false);
            return true;
        }, ct).ConfigureAwait(false);

    private async Task<Row?> FindByCustomer(DbConnection connection, DbTransaction transaction,
        Scope scope, string customerId, CancellationToken ct)
        => await connection.QuerySingleOrDefaultAsync<Row>(Command("""
            SELECT CONTACT_ID AS ContactId, VERSION AS Version, MDM_CUSTOMER_ID AS CustomerId,
                   CUSTOMER_NAME AS CustomerName, ENROLLED_BY AS EnrolledBy,
                   ENROLLED_AT_TICKS AS EnrolledAt
              FROM CRM_CUSTOMER_ENROLLMENT
             WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND MDM_CUSTOMER_ID=@CustomerId
            """, new { scope.TenantId, scope.OrganizationId, CustomerId = customerId }, transaction, ct));

    private Task Audit(DbConnection connection, DbTransaction transaction, Scope scope, string actor,
        string operation, Guid contactId, Guid? previous, Guid? version, DateTimeOffset at, CancellationToken ct)
        => ExecuteOne(connection, transaction, """
            INSERT INTO CRM_AUDIT
                (TENANT_ID,ORGANIZATION_ID,CHANGE_ID,ACTOR_USER_ID,OPERATION,ENTITY_ID,
                 PREVIOUS_VERSION,VERSION,RECORDED_AT_TICKS)
            VALUES (@TenantId,@OrganizationId,@Id,@Actor,@Operation,@EntityId,
                    @PreviousVersion,@Version,@RecordedAt)
            """, new
            {
                scope.TenantId,
                scope.OrganizationId,
                Id = Guid.NewGuid().ToString("D"),
                Actor = actor,
                Operation = operation,
                EntityId = contactId.ToString("D"),
                PreviousVersion = previous?.ToString("D"),
                Version = version?.ToString("D"),
                RecordedAt = at.UtcDateTime.Ticks,
            }, "CRM_STORAGE_CONTRACT_VIOLATION", ct);

    private static object Values(Scope scope, CrmCustomerEnrollment value) => new
    {
        scope.TenantId,
        scope.OrganizationId,
        ContactId = value.ContactId.ToString("D"),
        Version = value.Version.ToString("D"),
        value.CustomerId,
        value.CustomerName,
        EnrolledBy = value.EnrolledByUserId,
        EnrolledAt = value.EnrolledAt.UtcDateTime.Ticks,
    };

    private async Task ExecuteOne(DbConnection connection, DbTransaction transaction, string sql,
        object values, string code, CancellationToken ct)
    {
        if (await connection.ExecuteAsync(Command(sql, values, transaction, ct)) != 1)
            throw new BusinessException(code);
    }

    private CommandDefinition Command(string sql, object values, DbTransaction transaction, CancellationToken ct)
        => new(sql, values, transaction, _timeout, cancellationToken: ct);

    private static void RequireCustomerId(string customerId)
    {
        if (string.IsNullOrWhiteSpace(customerId) || customerId.Length > 50 || customerId != customerId.Trim())
            throw new BusinessException("INVALID_MDM_CUSTOMER_ID");
    }

    private sealed record Scope(Guid TenantGuid, Guid OrganizationGuid, string TenantId, string OrganizationId)
    {
        public static Scope Parse(BusinessScope value)
        {
            if (!Guid.TryParseExact(value.TenantId, "D", out var tenant) || tenant == Guid.Empty
                || !Guid.TryParseExact(value.OrganizationId, "D", out var organization) || organization == Guid.Empty)
                throw new BusinessException("INVALID_CRM_SCOPE");
            return new(tenant, organization, tenant.ToString("D"), organization.ToString("D"));
        }
    }

    private sealed class Row
    {
        public string ContactId { get; init; } = "";
        public string Version { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string CustomerName { get; init; } = "";
        public string EnrolledBy { get; init; } = "";
        public long EnrolledAt { get; init; }

        public CrmCustomerEnrollment Value()
        {
            if (!Guid.TryParseExact(ContactId, "D", out var contactId) || contactId == Guid.Empty
                || !Guid.TryParseExact(Version, "D", out var version) || version == Guid.Empty
                || string.IsNullOrWhiteSpace(CustomerId) || CustomerId.Length > 50 || CustomerId != CustomerId.Trim()
                || string.IsNullOrWhiteSpace(CustomerName) || CustomerName.Length > 255
                || string.IsNullOrWhiteSpace(EnrolledBy))
                throw new BusinessException("CRM_STORAGE_CONTRACT_VIOLATION");
            try
            {
                return new(contactId, version, CustomerId, CustomerName, EnrolledBy,
                    new DateTimeOffset(EnrolledAt, TimeSpan.Zero));
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new BusinessException("CRM_STORAGE_CONTRACT_VIOLATION");
            }
        }
    }
}
