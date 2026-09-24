using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Dapper;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaFramework.Service.Erp;
using NexaOne.ServiceContracts.Erp;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    private const int ShareTokenBytes = 32;

    public Task<BillingDocumentView> GetDocumentViewAsync(string userId, Guid tenantId,
        Guid organizationId, Guid documentId, CancellationToken ct = default)
        => Run(userId, tenantId, organizationId, "billing.read",
            (_, session) => session.DocumentView(documentId, ct), ct);

    public Task<BillingShareSecret> CreateShareAsync(string userId, Guid tenantId,
        Guid organizationId, Guid operationId, Guid documentId, DateTimeOffset expiresAt,
        CancellationToken ct = default)
        => RunDistribution(userId, tenantId, organizationId, "billing.deliver",
            session => session.CreateShare(operationId, documentId, expiresAt, ct), ct);

    public Task<BusinessPage<BillingShareLink>> ListSharesAsync(string userId, Guid tenantId,
        Guid organizationId, Guid documentId, int offset = 0, int limit = 50,
        CancellationToken ct = default)
    {
        ValidatePage(offset, limit);
        return RunDistribution(userId, tenantId, organizationId, "billing.read",
            session => session.ListShares(documentId, offset, limit, ct), ct);
    }

    public Task<BillingShareLink> RevokeShareAsync(string userId, Guid tenantId,
        Guid organizationId, Guid shareId, Guid version, CancellationToken ct = default)
        => RunDistribution(userId, tenantId, organizationId, "billing.deliver",
            session => session.RevokeShare(shareId, version, ct), ct);

    public Task<BusinessPage<BillingShareAccess>> ListShareAccessAsync(string userId, Guid tenantId,
        Guid organizationId, Guid shareId, int offset = 0, int limit = 50,
        CancellationToken ct = default)
    {
        ValidatePage(offset, limit);
        return RunDistribution(userId, tenantId, organizationId, "billing.read",
            session => session.ListShareAccess(shareId, offset, limit, ct), ct);
    }

    public Task<BusinessPage<BillingShareDelivery>> ListShareDeliveriesAsync(string userId,
        Guid tenantId, Guid organizationId, Guid shareId, int offset = 0, int limit = 50,
        CancellationToken ct = default)
    {
        ValidatePage(offset, limit);
        return RunDistribution(userId, tenantId, organizationId, "billing.read",
            session => session.ListShareDeliveries(shareId, offset, limit, ct), ct);
    }

    public Task<BillingShareDelivery> QueueShareDeliveryAsync(string userId, Guid tenantId,
        Guid organizationId, Guid operationId, Guid shareId, string token, Guid templateId,
        Guid profileId, string recipient, string publicUrl, DateTimeOffset? scheduledAt = null,
        CancellationToken ct = default)
        => RunDistribution(userId, tenantId, organizationId, "billing.deliver",
            session => session.QueueShareDelivery(operationId, shareId, token, templateId, profileId,
                recipient, publicUrl, scheduledAt, ct), ct);

    public Task<PublicBillingDocument> OpenPublicShareAsync(string token, string? clientAddress,
        string? userAgent, CancellationToken ct = default)
    {
        var tokenHash = TokenHash(token);
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var command = new CommandDefinition("""
                SELECT TENANT_ID AS TenantId, ORGANIZATION_ID AS OrganizationId
                  FROM ERP_BILLING_SHARE_LINK WHERE TOKEN_HASH=@TokenHash
                """, new { TokenHash = tokenHash }, transaction, commandTimeout: _timeout,
                cancellationToken: ct);
            var scope = await connection.QuerySingleOrDefaultAsync<ShareScopeRow>(command)
                ?? throw Failure("BILLING_SHARE_NOT_FOUND");
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", scope.TenantId, scope.OrganizationId), _memberships, _masters, _clock);
            try { return await session.OpenPublicShare(tokenHash, clientAddress, userAgent, ct); }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private Task<T> RunDistribution<T>(string userId, Guid tenantId, Guid organizationId,
        string permission, Func<Session, Task<T>> action, CancellationToken ct)
    {
        if (!ValidText(userId, 50) || tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw Failure("BUSINESS_ACCESS_DENIED");
        return _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
        {
            var session = new Session(connection, transaction, _timeout,
                new("NexaOne.MES", Text(tenantId), Text(organizationId)), _memberships, _masters, _clock);
            try
            {
                await session.Authorize(userId, permission, ct);
                var result = await action(session);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private static void ValidatePage(int offset, int limit)
    {
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
    }

    private static string TokenHash(string token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length != 43 || token != token.Trim()
            || token.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw Failure("BILLING_SHARE_NOT_FOUND");
        return Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(token))).ToLowerInvariant();
    }

    private static string NewToken()
        => Convert.ToBase64String(RandomNumberGenerator.GetBytes(ShareTokenBytes))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class ShareScopeRow
    {
        public string TenantId { get; set; } = "";
        public string OrganizationId { get; set; } = "";
    }

    private sealed partial class Session
    {
        private const string ShareColumns = "SHARE_ID AS Id, VERSION AS Version, OPERATION_ID AS OperationId, "
            + "DOCUMENT_ID AS DocumentId, DOCUMENT_VERSION AS DocumentVersion, EXPIRES_AT_TICKS AS ExpiresAt, "
            + "CREATED_BY AS CreatedBy, CREATED_AT_TICKS AS CreatedAt, REVOKED_AT_TICKS AS RevokedAt, "
            + "ACCESS_COUNT AS AccessCount, LAST_ACCESSED_AT_TICKS AS LastAccessedAt";

        internal async Task<BillingDocumentView> DocumentView(Guid documentId, CancellationToken ct)
        {
            var document = await FindDocumentAsync(documentId, ct)
                ?? throw Failure("BILLING_DOCUMENT_NOT_FOUND");
            var contact = await ContactRow("CONTACT_ID=@key", Text(document.Input.ContactId), ct)
                ?? throw Failure("STORAGE_CONTRACT_VIOLATION");
            var master = await masters.FindCustomerAsync(transaction, contact.CustomerId, ct)
                ?? throw Failure("STORAGE_CONTRACT_VIOLATION");
            return new(document, new(Id(contact.Id), Id(contact.Version), master.CustomerId,
                master.CustomerName, master.IsActive));
        }

        internal async Task<BillingShareSecret> CreateShare(Guid operationId, Guid documentId,
            DateTimeOffset expiresAt, CancellationToken ct)
        {
            if (operationId == Guid.Empty || documentId == Guid.Empty)
                throw Failure("INVALID_BUSINESS_INPUT");
            expiresAt = expiresAt.ToUniversalTime();
            var now = clock.GetUtcNow();
            if (expiresAt < now.AddMinutes(5) || expiresAt > now.AddDays(365))
                throw Failure("INVALID_BUSINESS_INPUT");
            var replay = await ReadShare("OPERATION_ID=@Key", operationId, ct);
            if (replay is not null)
            {
                if (replay.Link.DocumentId != documentId || replay.Link.ExpiresAt != expiresAt)
                    throw Failure("BILLING_OPERATION_REUSED");
                return new(replay.Link, null);
            }

            var view = await DocumentView(documentId, ct);
            if (view.Document.Status is BillingStatus.Draft or BillingStatus.Void)
                throw Failure("BILLING_DOCUMENT_NOT_DELIVERABLE");
            var token = NewToken();
            var link = new BillingShareLink(Guid.NewGuid(), Guid.NewGuid(), operationId, documentId,
                view.Document.Version, expiresAt, Actor.UserId, now);
            await Write("""
                INSERT INTO ERP_BILLING_SHARE_LINK
                    (TENANT_ID,ORGANIZATION_ID,SHARE_ID,VERSION,OPERATION_ID,DOCUMENT_ID,DOCUMENT_VERSION,
                     TOKEN_HASH,EXPIRES_AT_TICKS,CREATED_BY,CREATED_AT_TICKS,REVOKED_AT_TICKS,
                     ACCESS_COUNT,LAST_ACCESSED_AT_TICKS,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@Document,@DocumentVersion,
                        @TokenHash,@ExpiresAt,@CreatedBy,@CreatedAt,NULL,0,NULL,@Payload)
                """, new
                {
                    Id = Text(link.Id), Version = Text(link.Version), Operation = Text(operationId),
                    Document = Text(documentId), DocumentVersion = Text(view.Document.Version),
                    TokenHash = BillingBridge.TokenHash(token), ExpiresAt = expiresAt.UtcTicks,
                    link.CreatedBy, CreatedAt = now.UtcTicks, Payload = Serialize(view)
                }, ct);
            await AppendAuditAsync(new(Guid.NewGuid(), Actor, "billing-share", link.Id, "created",
                null, link.Version, now), ct);
            return new(link, token);
        }

        internal async Task<BusinessPage<BillingShareLink>> ListShares(Guid documentId, int offset,
            int limit, CancellationToken ct)
        {
            if (documentId == Guid.Empty) throw Failure("INVALID_BUSINESS_INPUT");
            if (await FindDocumentAsync(documentId, ct) is null) throw Failure("BILLING_DOCUMENT_NOT_FOUND");
            var values = new { Document = Text(documentId), Offset = offset, End = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_SHARE_LINK WHERE "
                + ScopeWhere + " AND DOCUMENT_ID=@Document", values, ct);
            var rows = await Rows<ShareRow>("SELECT * FROM (SELECT " + ShareColumns
                + ", ROW_NUMBER() OVER (ORDER BY CREATED_AT_TICKS, SHARE_ID) AS RowNumber "
                + "FROM ERP_BILLING_SHARE_LINK WHERE " + ScopeWhere
                + " AND DOCUMENT_ID=@Document) page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber",
                values, ct);
            return new(Array.AsReadOnly(rows.Select(Share).ToArray()), total);
        }

        internal async Task<BillingShareLink> RevokeShare(Guid shareId, Guid version, CancellationToken ct)
        {
            var current = await ReadShare("SHARE_ID=@Key", shareId, ct)
                ?? throw Failure("BILLING_SHARE_NOT_FOUND");
            if (current.Link.Version != version) throw Failure("BUSINESS_VERSION_CONFLICT");
            if (current.Link.RevokedAt is not null) return current.Link;
            var now = clock.GetUtcNow();
            var next = current.Link with { Version = Guid.NewGuid(), RevokedAt = now };
            await Write("UPDATE ERP_BILLING_SHARE_LINK SET VERSION=@Version,REVOKED_AT_TICKS=@Revoked "
                + "WHERE " + ScopeWhere + " AND SHARE_ID=@Id AND VERSION=@Previous AND REVOKED_AT_TICKS IS NULL",
                new { Id = Text(shareId), Version = Text(next.Version), Previous = Text(version), Revoked = now.UtcTicks }, ct);
            await AppendAuditAsync(new(Guid.NewGuid(), Actor, "billing-share", shareId, "revoked",
                version, next.Version, now), ct);
            return next;
        }

        internal async Task<BusinessPage<BillingShareAccess>> ListShareAccess(Guid shareId, int offset,
            int limit, CancellationToken ct)
        {
            if (await ReadShare("SHARE_ID=@Key", shareId, ct) is null)
                throw Failure("BILLING_SHARE_NOT_FOUND");
            var values = new { Share = Text(shareId), Offset = offset, End = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_SHARE_ACCESS WHERE "
                + ScopeWhere + " AND SHARE_ID=@Share", values, ct);
            var rows = await Rows<AccessRow>("""
                SELECT * FROM (
                    SELECT ACCESS_ID AS Id, SHARE_ID AS ShareId, ACCESSED_AT_TICKS AS AccessedAt,
                           CLIENT_ADDRESS_HASH AS ClientAddressHash, USER_AGENT AS UserAgent,
                           ROW_NUMBER() OVER (ORDER BY ACCESSED_AT_TICKS, ACCESS_ID) AS RowNumber
                      FROM ERP_BILLING_SHARE_ACCESS
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND SHARE_ID=@Share
                ) page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber
                """, values, ct);
            return new(Array.AsReadOnly(rows.Select(row => new BillingShareAccess(Id(row.Id), Id(row.ShareId),
                Utc(row.AccessedAt), row.ClientAddressHash, row.UserAgent)).ToArray()), total);
        }

        internal async Task<BillingShareDelivery> QueueShareDelivery(Guid operationId, Guid shareId,
            string token, Guid templateId, Guid profileId, string recipient, string publicUrl,
            DateTimeOffset? scheduledAt, CancellationToken ct)
        {
            var current = await ReadShare("SHARE_ID=@Key", shareId, ct)
                ?? throw Failure("BILLING_SHARE_NOT_FOUND");
            EnsureUsable(current.Link);
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(current.TokenHash), Encoding.ASCII.GetBytes(BillingBridge.TokenHash(token))))
                throw Failure("BILLING_SHARE_NOT_FOUND");
            if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback)
                || publicUrl.Length > 2048 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                || !uri.AbsolutePath.EndsWith($"/public/{Uri.EscapeDataString(token)}/pdf", StringComparison.Ordinal)
                || templateId == Guid.Empty || profileId == Guid.Empty)
                throw Failure("INVALID_BUSINESS_INPUT");
            var template = await FindDeliveryTemplateAsync(templateId, ct)
                ?? throw Failure("DELIVERY_TEMPLATE_NOT_FOUND");
            var snapshot = Deserialize<BillingDocumentView>(current.Payload);
            var variables = template.Variables.Select(name => new DeliveryVariable(name, name switch
            {
                "documentNumber" => snapshot.Document.Number.ToString(CultureInfo.InvariantCulture),
                "documentKind" => snapshot.Document.Kind.ToString(),
                "customerName" => snapshot.Contact.Name,
                "publicUrl" => publicUrl,
                "expiresAt" => current.Link.ExpiresAt.ToString("O", CultureInfo.InvariantCulture),
                _ => throw Failure("BILLING_DELIVERY_TEMPLATE_UNSUPPORTED")
            })).ToArray();
            var input = new DeliveryQueueInput(templateId, profileId, recipient,
                Array.AsReadOnly(variables), scheduledAt);
            var delivery = await new DeliveryService(this, this, clock)
                .QueueAsync(Actor, operationId, input, ct);
            var inserted = await Execute("""
                INSERT INTO ERP_BILLING_SHARE_DELIVERY
                    (TENANT_ID,ORGANIZATION_ID,SHARE_ID,DELIVERY_ID,CREATED_AT_TICKS)
                SELECT @TenantId,@OrganizationId,@Share,@Delivery,@CreatedAt
                 WHERE NOT EXISTS (SELECT 1 FROM ERP_BILLING_SHARE_DELIVERY
                    WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND DELIVERY_ID=@Delivery)
                """, new { Share = Text(shareId), Delivery = Text(delivery.Id), CreatedAt = clock.GetUtcNow().UtcTicks }, ct);
            if (inserted is < 0 or > 1) throw Failure("STORAGE_CONTRACT_VIOLATION");
            return new(shareId, delivery);
        }

        internal async Task<BusinessPage<BillingShareDelivery>> ListShareDeliveries(Guid shareId,
            int offset, int limit, CancellationToken ct)
        {
            if (await ReadShare("SHARE_ID=@Key", shareId, ct) is null)
                throw Failure("BILLING_SHARE_NOT_FOUND");
            var values = new { Share = Text(shareId), Offset = offset, End = (long)offset + limit };
            var total = await Scalar<long>("SELECT COUNT(*) FROM ERP_BILLING_SHARE_DELIVERY WHERE "
                + ScopeWhere + " AND SHARE_ID=@Share", values, ct);
            var rows = await Rows<DeliveryPayloadRow>("""
                SELECT * FROM (
                    SELECT d.PAYLOAD AS Payload,
                           ROW_NUMBER() OVER (ORDER BY l.CREATED_AT_TICKS,l.DELIVERY_ID) AS RowNumber
                      FROM ERP_BILLING_SHARE_DELIVERY l
                      JOIN COL_DELIVERY_REQUEST d
                        ON d.TENANT_ID=l.TENANT_ID AND d.ORGANIZATION_ID=l.ORGANIZATION_ID
                       AND d.DELIVERY_ID=l.DELIVERY_ID
                     WHERE l.TENANT_ID=@TenantId AND l.ORGANIZATION_ID=@OrganizationId
                       AND l.SHARE_ID=@Share
                ) page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber
                """, values, ct);
            return new(Array.AsReadOnly(rows.Select(row => new BillingShareDelivery(shareId,
                Deserialize<DeliveryRequest>(row.Payload))).ToArray()), total);
        }

        internal async Task<PublicBillingDocument> OpenPublicShare(string tokenHash,
            string? clientAddress, string? userAgent, CancellationToken ct)
        {
            var current = await ReadShare("TOKEN_HASH=@Key", tokenHash, ct)
                ?? throw Failure("BILLING_SHARE_NOT_FOUND");
            EnsureUsable(current.Link);
            var now = clock.GetUtcNow();
            var next = current.Link with
            {
                AccessCount = checked(current.Link.AccessCount + 1),
                LastAccessedAt = now
            };
            var normalizedAddress = string.IsNullOrWhiteSpace(clientAddress) ? null : clientAddress.Trim();
            if (normalizedAddress?.Length > 64) normalizedAddress = null;
            var addressHash = normalizedAddress is null ? null
                : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(tokenHash + ":" + normalizedAddress)))
                    .ToLowerInvariant();
            var agent = NormalizeUserAgent(userAgent);
            await Write("""
                INSERT INTO ERP_BILLING_SHARE_ACCESS
                    (TENANT_ID,ORGANIZATION_ID,ACCESS_ID,SHARE_ID,ACCESSED_AT_TICKS,CLIENT_ADDRESS_HASH,USER_AGENT)
                VALUES (@TenantId,@OrganizationId,@Id,@Share,@At,@AddressHash,@UserAgent)
                """, new { Id = Text(Guid.NewGuid()), Share = Text(next.Id), At = now.UtcTicks, AddressHash = addressHash, UserAgent = agent }, ct);
            await Write("UPDATE ERP_BILLING_SHARE_LINK SET ACCESS_COUNT=@Count,LAST_ACCESSED_AT_TICKS=@At "
                + "WHERE " + ScopeWhere + " AND SHARE_ID=@Id AND VERSION=@Version",
                new { Id = Text(next.Id), Version = Text(next.Version), Count = next.AccessCount, At = now.UtcTicks }, ct);
            return new(Deserialize<BillingDocumentView>(current.Payload), next);
        }

        private void EnsureUsable(BillingShareLink link)
        {
            if (link.RevokedAt is not null || link.ExpiresAt <= clock.GetUtcNow())
                throw Failure("BILLING_SHARE_NOT_FOUND");
        }

        private async Task<StoredShare?> ReadShare(string predicate, object key, CancellationToken ct)
        {
            var row = await Row<SharePayloadRow>("SELECT " + ShareColumns
                + ", TOKEN_HASH AS TokenHash, PAYLOAD AS Payload FROM ERP_BILLING_SHARE_LINK WHERE "
                + ScopeWhere + " AND " + predicate, new { Key = key is Guid id ? Text(id) : key }, ct);
            return row is null ? null : new(Share(row), row.TokenHash, row.Payload);
        }

        private static BillingShareLink Share(ShareRow row)
            => new(Id(row.Id), Id(row.Version), Id(row.OperationId), Id(row.DocumentId),
                Id(row.DocumentVersion), Utc(row.ExpiresAt), Text(Id(row.CreatedBy)), Utc(row.CreatedAt),
                row.RevokedAt.HasValue ? Utc(row.RevokedAt.Value) : null, row.AccessCount,
                row.LastAccessedAt.HasValue ? Utc(row.LastAccessedAt.Value) : null);

        private static DateTimeOffset Utc(long ticks) => new(ticks, TimeSpan.Zero);

        private static string? NormalizeUserAgent(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            var normalized = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
            return normalized.Length <= 256 ? normalized : normalized[..256];
        }

        private record StoredShare(BillingShareLink Link, string TokenHash, string Payload);
        private class ShareRow
        {
            public string Id { get; set; } = "";
            public string Version { get; set; } = "";
            public string OperationId { get; set; } = "";
            public string DocumentId { get; set; } = "";
            public string DocumentVersion { get; set; } = "";
            public long ExpiresAt { get; set; }
            public string CreatedBy { get; set; } = "";
            public long CreatedAt { get; set; }
            public long? RevokedAt { get; set; }
            public long AccessCount { get; set; }
            public long? LastAccessedAt { get; set; }
        }
        private sealed class SharePayloadRow : ShareRow
        {
            public string TokenHash { get; set; } = "";
            public string Payload { get; set; } = "";
        }
        private sealed class AccessRow
        {
            public string Id { get; set; } = "";
            public string ShareId { get; set; } = "";
            public long AccessedAt { get; set; }
            public string? ClientAddressHash { get; set; }
            public string? UserAgent { get; set; }
        }
        private sealed class DeliveryPayloadRow
        {
            public string Payload { get; set; } = "";
        }
    }
}
