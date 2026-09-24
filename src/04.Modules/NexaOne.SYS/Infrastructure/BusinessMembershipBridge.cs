using System.Data;
using System.Data.Common;
using Dapper;
using NexaDB.Data.Abstractions.Models;
using NexaOne.Common;
using NexaOne.Common.Security;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.SYS.Infrastructure;

/// <summary>Owns business membership, stable MES-user mapping, and its atomic revision history.</summary>
public sealed class BusinessMembershipBridge : QueryRepository, IBusinessMembershipBridge
{
    private const string ServiceRoleId = "ERP_RECURRING_SERVICE";
    // Explicit operation grants; '*' and MES role permissions never imply a business scope grant.
    private static readonly HashSet<string> SupportedPermissions = new(StringComparer.Ordinal)
    {
        "stock.warehouse.read", "stock.warehouse.write", "stock.product.write", "stock.read", "stock.post",
        "stock.reverse", "stock.reserve", "stock.consume", "stock.release",
        "equipment.read", "equipment.write", "equipment.booking.read", "equipment.booking.request",
        "equipment.booking.decide", "equipment.booking.cancel", "equipment.booking.checkout",
        "equipment.booking.return",
        "billing.read", "billing.write", "billing.decide", "billing.pay", "billing.credit", "billing.deliver",
        "expense.directory.read", "expense.directory.write", "expense.read", "expense.write",
        "expense.reimburse", "expense.invoice",
        "recurring.read", "recurring.write", "recurring.execute",
        "financial-report.read",
        "delivery.manage-template", "delivery.manage-profile", "delivery.queue", "delivery.read",
        "delivery.cancel", "delivery.manage-dead-letter",
        "crm.read", "crm.pipeline.manage", "crm.deal.manage", "crm.deal.delete",
        "crm.deal.all", "crm.deal.assigned", "crm.deal.created", "crm.deal.assigned-or-created",
        "crm.project.read", "crm.project.manage", "crm.project.delete", "crm.project.link-customer",
        "crm.project.all", "crm.project.assigned", "crm.project.created", "crm.project.assigned-or-created",
        "crm.team.read", "crm.team.manage", "crm.team.delete",
        "crm.customer.enroll",
    };
    private const string MembershipRowsSql = """
        SELECT m.TENANT_ID AS TenantId, m.ORGANIZATION_ID AS OrganizationId,
               m.USER_ID AS UserId, i.BUSINESS_USER_ID AS BusinessUserId,
               m.IS_ACTIVE AS IsActive, m.MEMBERSHIP_VERSION AS Version, m.PERMISSIONS AS Permissions
          FROM SYS_BUSINESS_MEMBERSHIP m
          JOIN SYS_BUSINESS_IDENTITY i ON i.USER_ID=m.USER_ID
          JOIN SYS_USER u ON u.USER_ID=m.USER_ID
        """;
    private const string MembershipSql = MembershipRowsSql
        + " WHERE m.TENANT_ID=@TenantId AND m.ORGANIZATION_ID=@OrganizationId AND m.USER_ID=@UserId";

    private readonly ServiceObjectProcessor _processor;
    private readonly int? _timeout;
    private readonly TimeProvider _time;
    private readonly string _listLimitSql;

    public BusinessMembershipBridge(EesDataSource dataSource, TimeProvider? time = null) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        _time = time ?? TimeProvider.System;
        _listLimitSql = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer
            ? " OFFSET 0 ROWS FETCH NEXT @Limit ROWS ONLY" : " LIMIT @Limit";
    }

    public async Task<BusinessMembership?> GetAccessAsync(
        string authenticatedUserId, Guid tenantId, Guid organizationId, CancellationToken ct = default)
    {
        if (!ValidKey(authenticatedUserId, tenantId, organizationId)) return null;
        var row = await QueryFirstOrDefaultAsync<MembershipRow>(
            MembershipSql + " AND m.IS_ACTIVE=1 AND u.IS_ACTIVE=1 AND u.IS_DELETED=0",
            Key(authenticatedUserId, tenantId, organizationId), ct);
        return row is null ? null : ToMembership(row);
    }

    public async Task<BusinessMembership?> GetAccessInTransactionAsync(
        DbTransaction transaction, string authenticatedUserId, Guid tenantId, Guid organizationId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidKey(authenticatedUserId, tenantId, organizationId)) return null;
        var row = await connection.QuerySingleOrDefaultAsync<MembershipRow>(Command(
            MembershipSql + " AND m.IS_ACTIVE=1 AND u.IS_ACTIVE=1 AND u.IS_DELETED=0",
            Key(authenticatedUserId, tenantId, organizationId), transaction, ct));
        return row is null ? null : ToMembership(row);
    }

    public Task<string> RequireAdministratorInTransactionAsync(
        DbTransaction transaction, string administratorId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return RequireAdministrator(RequireSerializableConnection(transaction), transaction, administratorId, ct);
    }

    public async Task<BusinessServiceActor> EnsureServiceActorInTransactionAsync(
        DbTransaction transaction, string administratorId, string serviceUserId, string displayName,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        var administrator = await RequireAdministrator(connection, transaction, administratorId, ct);
        if (!ValidServiceUser(serviceUserId) || string.IsNullOrWhiteSpace(displayName)
            || displayName.Length > 200 || displayName != displayName.Trim())
            throw new ArgumentException("Canonical service user ID and display name are required.");

        var now = _time.GetUtcNow().UtcDateTime;
        await connection.ExecuteAsync(Command("""
            INSERT INTO SYS_ROLE
                (ROLE_ID, ROLE_NAME, DESCRIPTION, PERMISSIONS, IS_DELETED,
                 CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @RoleId, 'ERP recurring service',
                   'Inactive compatibility identity for business audit foreign keys', '', 0,
                   @Administrator, @Now, @Administrator, @Now
             WHERE NOT EXISTS (SELECT 1 FROM SYS_ROLE WHERE ROLE_ID=@RoleId)
            """, new { RoleId = ServiceRoleId, Administrator = administrator, Now = now }, transaction, ct));
        var role = await connection.QuerySingleOrDefaultAsync<ServiceRoleRow>(Command("""
            SELECT ROLE_ID AS RoleId, PERMISSIONS AS Permissions, IS_DELETED AS IsDeleted
              FROM SYS_ROLE WHERE ROLE_ID=@RoleId
            """, new { RoleId = ServiceRoleId }, transaction, ct));
        if (role is null || role.IsDeleted || role.Permissions.Length != 0
            || !string.Equals(role.RoleId, ServiceRoleId, StringComparison.Ordinal))
            throw new InvalidDataException("Service compatibility role must exist with no permissions.");

        var user = await connection.QuerySingleOrDefaultAsync<ServiceActorRow>(Command("""
            SELECT u.USER_ID AS UserId, u.ROLE_ID AS RoleId, u.IS_ACTIVE AS IsActive,
                   u.IS_DELETED AS IsDeleted, i.BUSINESS_USER_ID AS BusinessUserId,
                   (SELECT COUNT(*) FROM SYS_BUSINESS_MEMBERSHIP m WHERE m.USER_ID=u.USER_ID) AS MembershipCount
              FROM SYS_USER u
              LEFT JOIN SYS_BUSINESS_IDENTITY i ON i.USER_ID=u.USER_ID
             WHERE u.USER_ID=@ServiceUserId
            """, new { ServiceUserId = serviceUserId }, transaction, ct));
        if (user is null)
        {
            var businessActorId = Guid.NewGuid();
            await ExecuteOne(connection, transaction, """
                INSERT INTO SYS_USER
                    (USER_ID, USER_NAME, PASSWORD_HASH, EMAIL, ROLE_ID, LANGUAGE, IS_ACTIVE, IS_DELETED,
                     CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
                VALUES (@ServiceUserId, @DisplayName, @PasswordHash, '', @RoleId, 'KoKr', 0, 0,
                        @Administrator, @Now, @Administrator, @Now)
                """, new { ServiceUserId = serviceUserId, DisplayName = displayName,
                    PasswordHash = new string('0', 64), RoleId = ServiceRoleId,
                    Administrator = administrator, Now = now }, ct);
            await ExecuteOne(connection, transaction, """
                INSERT INTO SYS_BUSINESS_IDENTITY (USER_ID, BUSINESS_USER_ID, CREATED_BY, CREATED_AT)
                VALUES (@ServiceUserId, @BusinessUserId, @Administrator, @Now)
                """, new { ServiceUserId = serviceUserId, BusinessUserId = businessActorId.ToString("D"),
                    Administrator = administrator, Now = now }, ct);
            return new(serviceUserId, businessActorId);
        }

        if (!string.Equals(user.UserId, serviceUserId, StringComparison.Ordinal)
            || !string.Equals(user.RoleId, ServiceRoleId, StringComparison.Ordinal)
            || user.IsActive || user.IsDeleted || user.MembershipCount != 0
            || string.IsNullOrEmpty(user.BusinessUserId))
            throw new InvalidDataException("Service compatibility identity is unsafe or corrupt.");
        return new(user.UserId, ParseId(user.BusinessUserId));
    }

    public async Task<IReadOnlyList<BusinessMembership>> ListAccessInTransactionAsync(
        DbTransaction transaction, string authenticatedUserId, Guid? afterTenantId = null,
        Guid? afterOrganizationId = null, int limit = 128, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (!ValidUser(authenticatedUserId)) throw new ArgumentException("A canonical authenticated user ID is required.", nameof(authenticatedUserId));
        if (afterTenantId.HasValue != afterOrganizationId.HasValue || afterTenantId == Guid.Empty || afterOrganizationId == Guid.Empty)
            throw new ArgumentException("The tenant and organization cursors must both be null or nonempty.", nameof(afterTenantId));
        if (limit is < 1 or > 128) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 128.");
        var rows = await connection.QueryAsync<MembershipRow>(Command(MembershipRowsSql
            + " WHERE m.USER_ID=@UserId AND m.IS_ACTIVE=1 AND u.IS_ACTIVE=1 AND u.IS_DELETED=0"
            + " AND (@AfterTenant IS NULL OR m.TENANT_ID>@AfterTenant OR (m.TENANT_ID=@AfterTenant AND m.ORGANIZATION_ID>@AfterOrganization))"
            + " ORDER BY m.TENANT_ID, m.ORGANIZATION_ID" + _listLimitSql,
            new { UserId = authenticatedUserId, AfterTenant = afterTenantId?.ToString("D"),
                AfterOrganization = afterOrganizationId?.ToString("D"), Limit = limit }, transaction, ct));
        ct.ThrowIfCancellationRequested();
        return Array.AsReadOnly(rows.Select(ToMembership).ToArray());
    }

    public async Task<BusinessMembership?> GetActiveMemberInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid businessUserId,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (tenantId == Guid.Empty || organizationId == Guid.Empty || businessUserId == Guid.Empty) return null;
        var row = await connection.QuerySingleOrDefaultAsync<MembershipRow>(Command(MembershipRowsSql
            + " WHERE m.TENANT_ID=@TenantId AND m.ORGANIZATION_ID=@OrganizationId AND i.BUSINESS_USER_ID=@BusinessUserId"
            + " AND m.IS_ACTIVE=1 AND u.IS_ACTIVE=1 AND u.IS_DELETED=0",
            new { TenantId = tenantId.ToString("D"), OrganizationId = organizationId.ToString("D"),
                BusinessUserId = businessUserId.ToString("D") }, transaction, ct));
        return row is null ? null : ToMembership(row);
    }

    public async Task<IReadOnlyList<BusinessMembership>> ListActiveMembersInTransactionAsync(
        DbTransaction transaction, Guid tenantId, Guid organizationId, Guid? afterBusinessUserId = null,
        int limit = 128, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = RequireSerializableConnection(transaction);
        if (tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw new ArgumentException("A nonempty tenant and organization are required.", nameof(tenantId));
        if (afterBusinessUserId == Guid.Empty)
            throw new ArgumentException("The business identity cursor must be nonempty.", nameof(afterBusinessUserId));
        if (limit is < 1 or > 128)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 128.");
        var rows = await connection.QueryAsync<MembershipRow>(Command(MembershipRowsSql
            + " WHERE m.TENANT_ID=@TenantId AND m.ORGANIZATION_ID=@OrganizationId"
            + " AND m.IS_ACTIVE=1 AND u.IS_ACTIVE=1 AND u.IS_DELETED=0"
            + " AND (@AfterBusinessUserId IS NULL OR i.BUSINESS_USER_ID>@AfterBusinessUserId)"
            + " ORDER BY i.BUSINESS_USER_ID" + _listLimitSql,
            new { TenantId = tenantId.ToString("D"), OrganizationId = organizationId.ToString("D"),
                AfterBusinessUserId = afterBusinessUserId?.ToString("D"), Limit = limit }, transaction, ct));
        ct.ThrowIfCancellationRequested();
        return Array.AsReadOnly(rows.Select(ToMembership).ToArray());
    }

    public Task<Result<BusinessMembership>> GetMembershipAsync(
        string administratorId, Guid tenantId, Guid organizationId, string userId,
        CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<BusinessMembership>>(async (connection, transaction) =>
        {
            await RequireAdministrator(connection, transaction, administratorId, ct);
            if (!ValidKey(userId, tenantId, organizationId)) return Invalid("Invalid membership key.");
            var row = await connection.QuerySingleOrDefaultAsync<MembershipRow>(Command(
                MembershipSql, Key(userId, tenantId, organizationId), transaction, ct));
            return row is null
                ? Result.Failure<BusinessMembership>(Error.NotFound("Business membership was not found."))
                : Result.Success(ToMembership(row));
        }, IsolationLevel.Serializable, ct);

    public Task<Result<BusinessMembership>> SaveMembershipAsync(
        string administratorId, Guid tenantId, Guid organizationId, string userId,
        BusinessMembershipChange change, CancellationToken ct = default)
        => _processor.ExecuteInTransactionAsync<Result<BusinessMembership>>(async (connection, transaction) =>
        {
            var administrator = await RequireAdministrator(connection, transaction, administratorId, ct);
            if (!ValidKey(userId, tenantId, organizationId) || change is null
                || change.ExpectedVersion < 0 || change.ExpectedVersion == long.MaxValue
                || !TryPermissions(change.Permissions, out var permissions))
                return Invalid("Invalid key, version, or business operation permissions.");

            var user = await connection.QuerySingleOrDefaultAsync<UserRow>(Command("""
                SELECT USER_ID AS UserId, IS_ACTIVE AS IsActive, IS_DELETED AS IsDeleted
                  FROM SYS_USER WHERE USER_ID=@userId
                """, new { userId }, transaction, ct));
            if (user is null || (change.IsActive && (!user.IsActive || user.IsDeleted)))
                return Invalid("An active membership requires an active, non-deleted SYS user.");

            // Use the stored canonical user key on both providers (including case-insensitive MSSQL).
            var key = Key(user.UserId, tenantId, organizationId);
            var current = await connection.QuerySingleOrDefaultAsync<MembershipRow>(Command(
                MembershipSql, key, transaction, ct));
            if ((current?.Version ?? 0) != change.ExpectedVersion)
                return Result.Failure<BusinessMembership>(Error.Conflict("Membership version changed; read before retrying."));
            if (current is not null) _ = ToMembership(current); // Corrupt stored grants never become authority.

            var businessUserId = await connection.QuerySingleOrDefaultAsync<string>(Command("""
                SELECT BUSINESS_USER_ID FROM SYS_BUSINESS_IDENTITY WHERE USER_ID=@UserId
                """, new { user.UserId }, transaction, ct));
            var now = _time.GetUtcNow().UtcDateTime;
            if (businessUserId is null)
            {
                businessUserId = Guid.NewGuid().ToString("D");
                await ExecuteOne(connection, transaction, """
                    INSERT INTO SYS_BUSINESS_IDENTITY (USER_ID, BUSINESS_USER_ID, CREATED_BY, CREATED_AT)
                    VALUES (@UserId, @businessUserId, @administrator, @now)
                    """, new { user.UserId, businessUserId, administrator, now }, ct);
            }
            var mappedId = ParseId(businessUserId);
            var revision = change.ExpectedVersion + 1;
            var values = new
            {
                key.TenantId, key.OrganizationId, key.UserId, change.IsActive,
                Permissions = string.Join('|', permissions), Version = revision,
                change.ExpectedVersion, Administrator = administrator, Now = now,
            };
            var sql = current is null ? """
                INSERT INTO SYS_BUSINESS_MEMBERSHIP
                    (TENANT_ID, ORGANIZATION_ID, USER_ID, IS_ACTIVE, PERMISSIONS,
                     MEMBERSHIP_VERSION, UPDATED_BY, UPDATED_AT)
                VALUES (@TenantId, @OrganizationId, @UserId, @IsActive, @Permissions,
                        @Version, @Administrator, @Now)
                """ : """
                UPDATE SYS_BUSINESS_MEMBERSHIP
                   SET IS_ACTIVE=@IsActive, PERMISSIONS=@Permissions, MEMBERSHIP_VERSION=@Version,
                       UPDATED_BY=@Administrator, UPDATED_AT=@Now
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND USER_ID=@UserId
                   AND MEMBERSHIP_VERSION=@ExpectedVersion
                """;
            await ExecuteOne(connection, transaction, sql, values, ct);
            await ExecuteOne(connection, transaction, """
                INSERT INTO SYS_BUSINESS_MEMBERSHIP_AUDIT
                    (TENANT_ID, ORGANIZATION_ID, USER_ID, MEMBERSHIP_VERSION, IS_ACTIVE,
                     PERMISSIONS, CHANGED_BY, CHANGED_AT)
                VALUES (@TenantId, @OrganizationId, @UserId, @Version, @IsActive,
                        @Permissions, @Administrator, @Now)
                """, values, ct);
            return Result.Success(new BusinessMembership(tenantId, organizationId, user.UserId,
                mappedId, change.IsActive, revision, permissions));
        }, IsolationLevel.Serializable, ct);

    private async Task<string> RequireAdministrator(
        DbConnection connection, DbTransaction transaction, string administratorId, CancellationToken ct)
    {
        if (!ValidUser(administratorId)) throw new UnauthorizedAccessException();
        var admin = await connection.QuerySingleOrDefaultAsync<AdministratorRow>(Command("""
            SELECT u.USER_ID AS UserId, r.PERMISSIONS AS Permissions
              FROM SYS_USER u JOIN SYS_ROLE r ON r.ROLE_ID=u.ROLE_ID
             WHERE u.USER_ID=@administratorId AND u.IS_ACTIVE=1 AND u.IS_DELETED=0 AND r.IS_DELETED=0
            """, new { administratorId }, transaction, ct));
        var grants = admin?.Permissions?.Split('|') ?? [];
        if (!grants.Contains(Permissions.SysManage, StringComparer.Ordinal)
            && !grants.Contains("*", StringComparer.Ordinal))
            throw new UnauthorizedAccessException();
        return admin!.UserId;
    }

    private static DbConnection RequireSerializableConnection(DbTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var connection = transaction.Connection;
        if (connection is null || connection.State != ConnectionState.Open
            || transaction.IsolationLevel != IsolationLevel.Serializable)
            throw new InvalidOperationException("A live Serializable transaction is required.");
        return connection;
    }

    private CommandDefinition Command(string sql, object parameters, DbTransaction transaction, CancellationToken ct)
        => new(sql, parameters, transaction, commandTimeout: _timeout, cancellationToken: ct);

    private async Task ExecuteOne(DbConnection connection, DbTransaction transaction,
        string sql, object parameters, CancellationToken ct)
    {
        if (await connection.ExecuteAsync(Command(sql, parameters, transaction, ct)) != 1)
            throw new DBConcurrencyException("Business membership write did not affect exactly one row.");
    }

    private static bool ValidUser(string value)
        => !string.IsNullOrWhiteSpace(value) && value.Length <= 50 && value == value.Trim();

    private static bool ValidServiceUser(string value)
        => ValidUser(value) && value.StartsWith("svc.", StringComparison.Ordinal)
            && value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9'
                or '.' or '-' or '_');

    private static bool ValidKey(string userId, Guid tenantId, Guid organizationId)
        => ValidUser(userId) && tenantId != Guid.Empty && organizationId != Guid.Empty;

    private static MembershipKey Key(string userId, Guid tenantId, Guid organizationId)
        => new(tenantId.ToString("D"), organizationId.ToString("D"), userId);

    private static Result<BusinessMembership> Invalid(string message)
        => Result.Failure<BusinessMembership>(Error.Validation(message));

    private static bool TryPermissions(IReadOnlyList<string>? values, out string[] permissions)
    {
        permissions = [];
        if (values is null || values.Count > SupportedPermissions.Count
            || values.Any(value => value is null || !SupportedPermissions.Contains(value))
            || values.Distinct(StringComparer.Ordinal).Count() != values.Count) return false;
        permissions = values.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return true;
    }
    private static Guid ParseId(string value)
        => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
            && value == id.ToString("D") ? id
            : throw new InvalidDataException("Business identity contains a non-canonical GUID.");

    private static BusinessMembership ToMembership(MembershipRow row)
    {
        if (row.Version <= 0 || !ValidUser(row.UserId) || row.Permissions is null
            || !TryPermissions(row.Permissions.Length == 0 ? [] : row.Permissions.Split('|'), out var grants)
            || string.Join('|', grants) != row.Permissions)
            throw new InvalidDataException("Business membership contains invalid grants or version.");
        return new BusinessMembership(ParseId(row.TenantId), ParseId(row.OrganizationId), row.UserId,
            ParseId(row.BusinessUserId), row.IsActive, row.Version, grants);
    }

    private sealed record MembershipKey(string TenantId, string OrganizationId, string UserId);
    private sealed class UserRow
    {
        public string UserId { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsDeleted { get; set; }
    }
    private sealed class AdministratorRow
    {
        public string UserId { get; set; } = "";
        public string Permissions { get; set; } = "";
    }
    private sealed class ServiceRoleRow
    {
        public string RoleId { get; set; } = "";
        public string Permissions { get; set; } = "";
        public bool IsDeleted { get; set; }
    }
    private sealed class ServiceActorRow
    {
        public string UserId { get; set; } = "";
        public string RoleId { get; set; } = "";
        public bool IsActive { get; set; }
        public bool IsDeleted { get; set; }
        public string? BusinessUserId { get; set; }
        public long MembershipCount { get; set; }
    }
    private sealed class MembershipRow
    {
        public string TenantId { get; set; } = "";
        public string OrganizationId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string BusinessUserId { get; set; } = "";
        public bool IsActive { get; set; }
        public long Version { get; set; }
        public string Permissions { get; set; } = "";
    }
}
