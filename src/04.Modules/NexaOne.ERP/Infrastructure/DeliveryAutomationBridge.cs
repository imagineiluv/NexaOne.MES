using System.Data;
using System.Data.Common;
using Dapper;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.Common;
using NexaOne.ServiceContracts.Collaboration;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge : IDeliveryAutomationBridge
{
    private const string DeliveryServiceUserPrefix = "svc.del.";

    Task<Result<DeliveryServicePrincipal>> IDeliveryAutomationBridge.GetPrincipalAsync(
        string administratorId, string principalId, CancellationToken ct)
        => _processor.ExecuteInTransactionAsync<Result<DeliveryServicePrincipal>>(async (connection, transaction) =>
        {
            await _memberships.RequireAdministratorInTransactionAsync(transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId)) return InvalidDeliveryPrincipal();
            var row = await ReadDeliveryPrincipal(connection, transaction, principalId, ct);
            return row is null
                ? Result.Failure<DeliveryServicePrincipal>(Error.NotFound("Delivery service principal was not found."))
                : Result.Success(ToDeliveryPrincipal(row));
        }, IsolationLevel.Serializable, ct);

    public Task<Result<DeliveryServicePrincipal>> SavePrincipalAsync(
        string administratorId, string principalId, DeliveryServicePrincipalChange change,
        CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<DeliveryServicePrincipal>>(async (connection, transaction) =>
        {
            var administrator = await _memberships.RequireAdministratorInTransactionAsync(
                transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId) || change is null || change.ExpectedVersion < 0
                || change.ExpectedVersion == long.MaxValue || !ValidText(change.Name, 200))
                return InvalidDeliveryPrincipal();

            var current = await ReadDeliveryPrincipal(connection, transaction, principalId, ct);
            if ((current?.Version ?? 0) != change.ExpectedVersion)
                return Result.Failure<DeliveryServicePrincipal>(
                    Error.Conflict("Delivery service principal version changed; read before retrying."));
            if (current is not null) _ = ToDeliveryPrincipal(current);

            var serviceActor = await _memberships.EnsureServiceActorInTransactionAsync(
                transaction, administrator, DeliveryServiceUserPrefix + principalId, change.Name.Trim(), ct);
            var revision = change.ExpectedVersion + 1;
            var now = _clock.GetUtcNow().UtcDateTime;
            var auditActorId = serviceActor.BusinessActorId;
            if (current is not null && auditActorId != Id(current.AuditActorId))
                throw new InvalidDataException("Delivery service principal audit identity changed.");
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
                INSERT INTO COL_DELIVERY_SERVICE_PRINCIPAL
                    (PRINCIPAL_ID,AUDIT_ACTOR_ID,NAME,IS_ACTIVE,PRINCIPAL_VERSION,UPDATED_BY,UPDATED_AT)
                VALUES (@PrincipalId,@AuditActorId,@Name,@IsActive,@Version,@Administrator,@Now)
                """ : """
                UPDATE COL_DELIVERY_SERVICE_PRINCIPAL
                   SET NAME=@Name,IS_ACTIVE=@IsActive,PRINCIPAL_VERSION=@Version,
                       UPDATED_BY=@Administrator,UPDATED_AT=@Now
                 WHERE PRINCIPAL_ID=@PrincipalId AND PRINCIPAL_VERSION=@ExpectedVersion
                """, values, transaction, _timeout, cancellationToken: ct));
            if (affected != 1)
                throw new DBConcurrencyException("Delivery service principal write did not affect exactly one row.");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO COL_DELIVERY_SERVICE_PRINCIPAL_AUDIT
                    (PRINCIPAL_ID,PRINCIPAL_VERSION,NAME,IS_ACTIVE,CHANGED_BY,CHANGED_AT)
                VALUES (@PrincipalId,@Version,@Name,@IsActive,@Administrator,@Now)
                """, values, transaction, _timeout, cancellationToken: ct));
            return Result.Success(new DeliveryServicePrincipal(
                principalId, auditActorId, change.Name.Trim(), change.IsActive, revision));
        }, IsolationLevel.Serializable, ct);

    Task<Result<DeliveryServicePrincipalScope>> IDeliveryAutomationBridge.GetScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        CancellationToken ct)
        => _processor.ExecuteInTransactionAsync<Result<DeliveryServicePrincipalScope>>(async (connection, transaction) =>
        {
            await _memberships.RequireAdministratorInTransactionAsync(transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId) || tenantId == Guid.Empty || organizationId == Guid.Empty)
                return InvalidDeliveryScope();
            var row = await ReadDeliveryScope(connection, transaction, principalId, tenantId, organizationId, ct);
            return row is null
                ? Result.Failure<DeliveryServicePrincipalScope>(Error.NotFound("Delivery service principal scope was not found."))
                : Result.Success(ToDeliveryScope(row));
        }, IsolationLevel.Serializable, ct);

    public Task<Result<DeliveryServicePrincipalScope>> SaveScopeAsync(
        string administratorId, string principalId, Guid tenantId, Guid organizationId,
        DeliveryServicePrincipalScopeChange change, CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<DeliveryServicePrincipalScope>>(async (connection, transaction) =>
        {
            var administrator = await _memberships.RequireAdministratorInTransactionAsync(
                transaction, administratorId, ct);
            if (!ValidPrincipalId(principalId) || tenantId == Guid.Empty || organizationId == Guid.Empty
                || change is null || change.ExpectedVersion < 0 || change.ExpectedVersion == long.MaxValue)
                return InvalidDeliveryScope();
            if (await ReadDeliveryPrincipal(connection, transaction, principalId, ct) is not { } principal)
                return Result.Failure<DeliveryServicePrincipalScope>(Error.NotFound("Delivery service principal was not found."));
            _ = ToDeliveryPrincipal(principal);

            var current = await ReadDeliveryScope(connection, transaction, principalId, tenantId, organizationId, ct);
            if ((current?.Version ?? 0) != change.ExpectedVersion)
                return Result.Failure<DeliveryServicePrincipalScope>(
                    Error.Conflict("Delivery service principal scope version changed; read before retrying."));
            if (current is not null) _ = ToDeliveryScope(current);

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
                INSERT INTO COL_DELIVERY_SERVICE_SCOPE
                    (PRINCIPAL_ID,TENANT_ID,ORGANIZATION_ID,IS_ACTIVE,SCOPE_VERSION,UPDATED_BY,UPDATED_AT)
                VALUES (@PrincipalId,@TenantId,@OrganizationId,@IsActive,@Version,@Administrator,@Now)
                """ : """
                UPDATE COL_DELIVERY_SERVICE_SCOPE
                   SET IS_ACTIVE=@IsActive,SCOPE_VERSION=@Version,UPDATED_BY=@Administrator,UPDATED_AT=@Now
                 WHERE PRINCIPAL_ID=@PrincipalId AND TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                   AND SCOPE_VERSION=@ExpectedVersion
                """, values, transaction, _timeout, cancellationToken: ct));
            if (affected != 1)
                throw new DBConcurrencyException("Delivery service principal scope write did not affect exactly one row.");
            await connection.ExecuteAsync(new CommandDefinition("""
                INSERT INTO COL_DELIVERY_SERVICE_SCOPE_AUDIT
                    (PRINCIPAL_ID,TENANT_ID,ORGANIZATION_ID,SCOPE_VERSION,IS_ACTIVE,CHANGED_BY,CHANGED_AT)
                VALUES (@PrincipalId,@TenantId,@OrganizationId,@Version,@IsActive,@Administrator,@Now)
                """, values, transaction, _timeout, cancellationToken: ct));
            return Result.Success(new DeliveryServicePrincipalScope(
                principalId, tenantId, organizationId, change.IsActive, revision));
        }, IsolationLevel.Serializable, ct);

    Task<BusinessPage<DeliveryServicePrincipalScope>> IDeliveryAutomationBridge.ListActiveScopesAsync(
        string principalId, int offset, int limit, CancellationToken ct)
    {
        if (!ValidPrincipalId(principalId)) throw Failure("BUSINESS_ACCESS_DENIED");
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var principal = await ReadDeliveryPrincipal(connection, transaction, principalId, ct);
            if (principal is null || !ToDeliveryPrincipal(principal).IsActive)
                throw Failure("BUSINESS_ACCESS_DENIED");
            var count = await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
                SELECT COUNT(*) FROM COL_DELIVERY_SERVICE_SCOPE
                 WHERE PRINCIPAL_ID=@PrincipalId AND IS_ACTIVE=1
                """, new { PrincipalId = principalId }, transaction, _timeout, cancellationToken: ct));
            var rows = (await connection.QueryAsync<DeliveryScopeRow>(new CommandDefinition("""
                SELECT PRINCIPAL_ID AS PrincipalId,TENANT_ID AS TenantId,ORGANIZATION_ID AS OrganizationId,
                       IS_ACTIVE AS IsActive,SCOPE_VERSION AS Version
                  FROM (SELECT PRINCIPAL_ID,TENANT_ID,ORGANIZATION_ID,IS_ACTIVE,SCOPE_VERSION,
                               ROW_NUMBER() OVER (ORDER BY TENANT_ID,ORGANIZATION_ID) AS RowNumber
                          FROM COL_DELIVERY_SERVICE_SCOPE
                         WHERE PRINCIPAL_ID=@PrincipalId AND IS_ACTIVE=1) AS page
                 WHERE RowNumber>@Offset AND RowNumber<=@EndRow ORDER BY RowNumber
                """, new { PrincipalId = principalId, Offset = offset, EndRow = (long)offset + limit },
                transaction, _timeout, cancellationToken: ct))).Select(ToDeliveryScope).ToArray();
            return new BusinessPage<DeliveryServicePrincipalScope>(Array.AsReadOnly(rows), count);
        }, IsolationLevel.Serializable, ct);
    }

    public Task<IReadOnlyList<DeliveryRequest>> ClaimDueAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion,
        int limit, TimeSpan leaseDuration, CancellationToken ct = default)
        => RunDeliveryPrincipal(principalId, tenantId, organizationId, scopeVersion,
            (service, actor) => service.ClaimDueAsync(actor, limit, leaseDuration, ct), ct);

    public Task<DeliveryRequest> CompleteAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion,
        Guid deliveryId, Guid version, Guid leaseId, string providerReceipt,
        CancellationToken ct = default)
        => RunDeliveryPrincipal(principalId, tenantId, organizationId, scopeVersion,
            (service, actor) => service.CompleteAsync(actor, deliveryId, version, leaseId, providerReceipt, ct), ct);

    public Task<DeliveryRequest> FailAsync(
        string principalId, Guid tenantId, Guid organizationId, long scopeVersion,
        Guid deliveryId, Guid version, Guid leaseId, string errorCode,
        CancellationToken ct = default)
        => RunDeliveryPrincipal(principalId, tenantId, organizationId, scopeVersion,
            (service, actor) => service.FailAsync(actor, deliveryId, version, leaseId, errorCode, ct), ct);

    private Task<T> RunDeliveryPrincipal<T>(string principalId, Guid tenantId, Guid organizationId,
        long scopeVersion, Func<DeliveryService, BusinessActor, Task<T>> action, CancellationToken ct)
    {
        if (!ValidPrincipalId(principalId) || tenantId == Guid.Empty || organizationId == Guid.Empty
            || scopeVersion <= 0) throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock);
            try
            {
                await session.AuthorizeDeliveryPrincipal(principalId, scopeVersion, ct);
                var result = await action(new DeliveryService(session, session, _clock), session.Actor);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private Task<DeliveryPrincipalRow?> ReadDeliveryPrincipal(DbConnection connection, DbTransaction transaction,
        string principalId, CancellationToken ct)
        => connection.QuerySingleOrDefaultAsync<DeliveryPrincipalRow>(new CommandDefinition("""
            SELECT PRINCIPAL_ID AS PrincipalId,AUDIT_ACTOR_ID AS AuditActorId,NAME AS Name,
                   IS_ACTIVE AS IsActive,PRINCIPAL_VERSION AS Version
              FROM COL_DELIVERY_SERVICE_PRINCIPAL WHERE PRINCIPAL_ID=@PrincipalId
            """, new { PrincipalId = principalId }, transaction, _timeout, cancellationToken: ct));

    private Task<DeliveryScopeRow?> ReadDeliveryScope(DbConnection connection, DbTransaction transaction,
        string principalId, Guid tenantId, Guid organizationId, CancellationToken ct)
        => connection.QuerySingleOrDefaultAsync<DeliveryScopeRow>(new CommandDefinition("""
            SELECT PRINCIPAL_ID AS PrincipalId,TENANT_ID AS TenantId,ORGANIZATION_ID AS OrganizationId,
                   IS_ACTIVE AS IsActive,SCOPE_VERSION AS Version
              FROM COL_DELIVERY_SERVICE_SCOPE
             WHERE PRINCIPAL_ID=@PrincipalId AND TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
            """, new { PrincipalId = principalId, TenantId = Text(tenantId), OrganizationId = Text(organizationId) },
            transaction, _timeout, cancellationToken: ct));

    private static DeliveryServicePrincipal ToDeliveryPrincipal(DeliveryPrincipalRow row)
    {
        if (!ValidPrincipalId(row.PrincipalId) || !ValidText(row.Name, 200) || row.Version <= 0)
            throw new InvalidDataException("Delivery service principal storage is invalid.");
        return new(row.PrincipalId, Id(row.AuditActorId), row.Name, row.IsActive, row.Version);
    }

    private static DeliveryServicePrincipalScope ToDeliveryScope(DeliveryScopeRow row)
    {
        if (!ValidPrincipalId(row.PrincipalId) || row.Version <= 0)
            throw new InvalidDataException("Delivery service principal scope storage is invalid.");
        return new(row.PrincipalId, Id(row.TenantId), Id(row.OrganizationId), row.IsActive, row.Version);
    }

    private static Result<DeliveryServicePrincipal> InvalidDeliveryPrincipal()
        => Result.Failure<DeliveryServicePrincipal>(Error.Validation("Invalid delivery service principal."));
    private static Result<DeliveryServicePrincipalScope> InvalidDeliveryScope()
        => Result.Failure<DeliveryServicePrincipalScope>(Error.Validation("Invalid delivery service principal scope."));

    private sealed class DeliveryPrincipalRow
    {
        public string PrincipalId { get; set; } = "";
        public string AuditActorId { get; set; } = "";
        public string Name { get; set; } = "";
        public bool IsActive { get; set; }
        public long Version { get; set; }
    }

    private sealed class DeliveryScopeRow
    {
        public string PrincipalId { get; set; } = "";
        public string TenantId { get; set; } = "";
        public string OrganizationId { get; set; } = "";
        public bool IsActive { get; set; }
        public long Version { get; set; }
    }

    private sealed partial class Session
    {
        internal async Task AuthorizeDeliveryPrincipal(string principalId, long scopeVersion, CancellationToken ct)
        {
            var stored = await Row<DeliveryAuthorityRow>("""
                SELECT p.PRINCIPAL_ID AS PrincipalId,p.AUDIT_ACTOR_ID AS AuditActorId,
                       s.SCOPE_VERSION AS ScopeVersion
                  FROM COL_DELIVERY_SERVICE_PRINCIPAL p
                  JOIN COL_DELIVERY_SERVICE_SCOPE s ON s.PRINCIPAL_ID=p.PRINCIPAL_ID
                 WHERE p.PRINCIPAL_ID=@PrincipalId AND p.IS_ACTIVE=1 AND s.IS_ACTIVE=1
                   AND s.TENANT_ID=@TenantId AND s.ORGANIZATION_ID=@OrganizationId
                """, new { PrincipalId = principalId }, ct);
            if (stored is null || !string.Equals(stored.PrincipalId, principalId, StringComparison.Ordinal)
                || stored.ScopeVersion != scopeVersion)
                throw Failure("BUSINESS_ACCESS_DENIED");
            Actor = new(Text(Id(stored.AuditActorId)), Scope);
            _grants = ["delivery.dispatch"];
        }
    }

    private sealed class DeliveryAuthorityRow
    {
        public string PrincipalId { get; set; } = "";
        public string AuditActorId { get; set; } = "";
        public long ScopeVersion { get; set; }
    }
}
