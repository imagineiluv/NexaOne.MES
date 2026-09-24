using System.Data;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;
using NexaOne.ServiceContracts.Collaboration;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
    Task<BusinessPage<BusinessMembership>> IDeliveryBridge.ListAccessibleScopesAsync(
        string userId, int offset, int limit, CancellationToken ct)
    {
        if (!ValidText(userId, 50)) throw Failure("BUSINESS_ACCESS_DENIED");
        if (offset < 0 || limit is < 1 or > 100) throw Failure("INVALID_BUSINESS_INPUT");
        return _processor.ExecuteInTransactionAsync(async (_, transaction) =>
        {
            const int batchSize = 128;
            var items = new List<BusinessMembership>(limit);
            long total = 0;
            Guid? afterTenant = null, afterOrganization = null;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                IReadOnlyList<BusinessMembership> batch;
                try
                {
                    batch = await _memberships.ListAccessInTransactionAsync(transaction, userId,
                        afterTenant, afterOrganization, batchSize, ct);
                }
                catch (InvalidDataException) { throw Failure("BUSINESS_ACCESS_DENIED"); }
                if (batch is null || batch.Count > batchSize) throw Failure("STORAGE_CONTRACT_VIOLATION");
                foreach (var membership in batch)
                {
                    if (membership is null || membership.TenantId == Guid.Empty
                        || membership.OrganizationId == Guid.Empty || membership.BusinessUserId == Guid.Empty
                        || !membership.IsActive || membership.Version <= 0 || membership.Permissions is null)
                        throw Failure("STORAGE_CONTRACT_VIOLATION");
                    if (afterTenant.HasValue)
                    {
                        var tenantOrder = string.CompareOrdinal(Text(membership.TenantId), Text(afterTenant.Value));
                        if (tenantOrder < 0 || tenantOrder == 0
                            && string.CompareOrdinal(Text(membership.OrganizationId), Text(afterOrganization!.Value)) <= 0)
                            throw Failure("STORAGE_CONTRACT_VIOLATION");
                    }
                    afterTenant = membership.TenantId;
                    afterOrganization = membership.OrganizationId;
                    if (!membership.Permissions.Contains("delivery.read", StringComparer.Ordinal)) continue;
                    if (total++ >= offset && items.Count < limit) items.Add(membership);
                }
                if (batch.Count < batchSize) break;
            }
            ct.ThrowIfCancellationRequested();
            return new BusinessPage<BusinessMembership>(Array.AsReadOnly(items.ToArray()), total);
        }, IsolationLevel.Serializable, ct);
    }

    public Task<DeliveryTemplate> CreateTemplateAsync(string userId, Guid tenantId, Guid organizationId,
        string name, string subject, string body, IReadOnlyList<string>? variables = null,
        CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.manage-template",
            (service, actor) => service.CreateTemplateAsync(actor, name, subject, body, variables, ct), ct);

    public Task<DeliveryTemplate> DeactivateTemplateAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.manage-template",
            (service, actor) => service.DeactivateTemplateAsync(actor, id, version, ct), ct);

    public Task<DeliveryProfile> CreateProfileAsync(string userId, Guid tenantId, Guid organizationId,
        string name, string providerKey, string credentialReference, DeliveryRetryPolicy retryPolicy,
        CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.manage-profile",
            (service, actor) => service.CreateProfileAsync(actor, name, providerKey, credentialReference,
                retryPolicy, ct), ct);

    public Task<DeliveryProfile> DeactivateProfileAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.manage-profile",
            (service, actor) => service.DeactivateProfileAsync(actor, id, version, ct), ct);

    public Task<DeliveryRequest> QueueAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, DeliveryQueueInput input, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.queue",
            (service, actor) => service.QueueAsync(actor, operationId, input, ct), ct);

    public Task<DeliveryRequest> GetAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.read",
            (service, actor) => service.GetAsync(actor, id, ct), ct);

    public Task<DeliveryRequest> CancelAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.cancel",
            (service, actor) => service.CancelAsync(actor, id, version, ct), ct);

    public Task<BusinessPage<DeliveryRequest>> ListDeadLettersAsync(string userId, Guid tenantId,
        Guid organizationId, int offset = 0, int limit = 50, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.read",
            (service, actor) => service.ListDeadLettersAsync(actor, offset, limit, ct), ct);

    public Task<DeliveryRequest> RetryDeadLetterAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid id, Guid version, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.manage-dead-letter",
            (service, actor) => service.RetryDeadLetterAsync(actor, operationId, id, version, ct), ct);

    public Task<DeliveryRequest> DiscardDeadLetterAsync(string userId, Guid tenantId, Guid organizationId,
        Guid operationId, Guid id, Guid version, CancellationToken ct = default)
        => RunDelivery(userId, tenantId, organizationId, "delivery.manage-dead-letter",
            (service, actor) => service.DiscardDeadLetterAsync(actor, operationId, id, version, ct), ct);

    private Task<T> RunDelivery<T>(string userId, Guid tenantId, Guid organizationId, string permission,
        Func<DeliveryService, BusinessActor, Task<T>> action, CancellationToken ct)
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
                var result = await action(new DeliveryService(session, session, _clock), session.Actor);
                ct.ThrowIfCancellationRequested();
                return result;
            }
            finally { session.Close(); }
        }, IsolationLevel.Serializable, ct);
    }

    private sealed partial class Session
    {
        async Task<T> IAtomicBusinessStore<IDeliveryTransaction>.ExecuteAsync<T>(BusinessScope requestedScope,
            Func<IDeliveryTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            EnsureExecution(requestedScope, ct);
            var result = await work(this, ct); ct.ThrowIfCancellationRequested(); return result;
        }

        public Task<DeliveryTemplate?> FindDeliveryTemplateAsync(Guid id, CancellationToken ct)
            => ReadDelivery<DeliveryTemplate>("COL_DELIVERY_TEMPLATE", "TEMPLATE_ID", id, ct);

        public Task SaveDeliveryTemplateAsync(DeliveryTemplate value, Guid? expectedVersion, CancellationToken ct)
            => SaveDeliveryValue(value.Scope, "COL_DELIVERY_TEMPLATE", "TEMPLATE_ID", value.Id, value.Version,
                expectedVersion, Serialize(value), ct);

        public Task<DeliveryProfile?> FindDeliveryProfileAsync(Guid id, CancellationToken ct)
            => ReadDelivery<DeliveryProfile>("COL_DELIVERY_PROFILE", "PROFILE_ID", id, ct);

        public Task SaveDeliveryProfileAsync(DeliveryProfile value, Guid? expectedVersion, CancellationToken ct)
            => SaveDeliveryValue(value.Scope, "COL_DELIVERY_PROFILE", "PROFILE_ID", value.Id, value.Version,
                expectedVersion, Serialize(value), ct);

        public Task<DeliveryRequest?> FindDeliveryAsync(Guid id, CancellationToken ct)
            => ReadDelivery<DeliveryRequest>("COL_DELIVERY_REQUEST", "DELIVERY_ID", id, ct);

        public Task<DeliveryRequest?> FindDeliveryByOperationAsync(Guid operationId, CancellationToken ct)
            => ReadDelivery<DeliveryRequest>("COL_DELIVERY_REQUEST", "OPERATION_ID", operationId, ct);

        public Task<DeliveryDeadLetterOperation?> FindDeliveryDeadLetterOperationAsync(
            Guid operationId, CancellationToken ct)
            => ReadDelivery<DeliveryDeadLetterOperation>("COL_DELIVERY_DEAD_LETTER_OPERATION", "OPERATION_ID",
                operationId, ct);

        public async Task SaveDeliveryDeadLetterOperationAsync(
            DeliveryDeadLetterOperation value, CancellationToken ct)
        {
            RequireScope(value.Scope);
            await Write("""
                INSERT INTO COL_DELIVERY_DEAD_LETTER_OPERATION
                    (TENANT_ID,ORGANIZATION_ID,OPERATION_ID,DELIVERY_ID,ACTION,RESULT_VERSION,
                     ACTOR_ID,OCCURRED_AT_TICKS,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Operation,@Delivery,@Action,@ResultVersion,
                        @Actor,@OccurredAt,@Payload)
                """, new
                {
                    Operation = Text(value.OperationId), Delivery = Text(value.DeliveryId),
                    Action = (int)value.Action, ResultVersion = Text(value.ResultVersion),
                    Actor = value.ActorId, OccurredAt = value.OccurredAt.UtcTicks, Payload = Serialize(value)
                }, ct);
        }

        public async Task<BusinessPage<DeliveryRequest>> QueryDeadLetterDeliveriesAsync(
            int offset, int limit, CancellationToken ct)
        {
            var total = await Scalar<long>("SELECT COUNT(*) FROM COL_DELIVERY_REQUEST WHERE "
                + ScopeWhere + " AND STATE=4", null, ct);
            var rows = await Rows<PayloadRow>("""
                SELECT PAYLOAD AS Payload FROM (
                    SELECT PAYLOAD, ROW_NUMBER() OVER (ORDER BY DELIVERY_ID) AS RowNumber
                    FROM COL_DELIVERY_REQUEST
                    WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND STATE=4
                ) AS page WHERE RowNumber>@Offset AND RowNumber<=@End ORDER BY RowNumber
                """, new { Offset = offset, End = (long)offset + limit }, ct);
            return new BusinessPage<DeliveryRequest>(Array.AsReadOnly(rows
                .Select(row => Deserialize<DeliveryRequest>(row.Payload)).ToArray()), total);
        }

        public async Task SaveDeliveryAsync(DeliveryRequest value, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(value.Scope);
            var values = new
            {
                Id = Text(value.Id), Version = Text(value.Version), Operation = Text(value.OperationId),
                State = (int)value.State, NextAttempt = value.NextAttemptAt.UtcTicks,
                LeaseExpires = value.LeaseExpiresAt?.UtcTicks, Payload = Serialize(value),
                Previous = Text(expectedVersion)
            };
            await Write(expectedVersion is null ? """
                INSERT INTO COL_DELIVERY_REQUEST (TENANT_ID,ORGANIZATION_ID,DELIVERY_ID,VERSION,OPERATION_ID,
                    STATE,NEXT_ATTEMPT_AT_TICKS,LEASE_EXPIRES_AT_TICKS,PAYLOAD)
                VALUES (@TenantId,@OrganizationId,@Id,@Version,@Operation,@State,@NextAttempt,@LeaseExpires,@Payload)
                """ : "UPDATE COL_DELIVERY_REQUEST SET VERSION=@Version,STATE=@State,NEXT_ATTEMPT_AT_TICKS=@NextAttempt,"
                    + "LEASE_EXPIRES_AT_TICKS=@LeaseExpires,PAYLOAD=@Payload WHERE " + ScopeWhere
                    + " AND DELIVERY_ID=@Id AND VERSION=@Previous", values, ct);
        }

        public async Task<IReadOnlyList<DeliveryRequest>> QueryDispatchableDeliveriesAsync(
            DateTimeOffset now, int limit, CancellationToken ct)
        {
            var rows = await Rows<PayloadRow>("""
                SELECT PAYLOAD AS Payload FROM (
                    SELECT PAYLOAD, ROW_NUMBER() OVER (
                        ORDER BY CASE WHEN STATE=1 THEN LEASE_EXPIRES_AT_TICKS ELSE NEXT_ATTEMPT_AT_TICKS END,
                                 DELIVERY_ID) AS RowNumber
                    FROM COL_DELIVERY_REQUEST
                    WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                      AND ((STATE IN (0,2) AND NEXT_ATTEMPT_AT_TICKS<=@Now)
                        OR (STATE=1 AND LEASE_EXPIRES_AT_TICKS<=@Now))
                ) AS page WHERE RowNumber<=@Limit ORDER BY RowNumber
                """, new { Now = now.UtcTicks, Limit = limit }, ct);
            return Array.AsReadOnly(rows.Select(row => Deserialize<DeliveryRequest>(row.Payload)).ToArray());
        }

        private async Task<T?> ReadDelivery<T>(string table, string keyColumn, Guid key, CancellationToken ct)
            where T : class
        {
            var payload = await Scalar<string?>($"SELECT PAYLOAD FROM {table} WHERE {ScopeWhere} AND {keyColumn}=@Key",
                new { Key = Text(key) }, ct);
            return payload is null ? null : Deserialize<T>(payload);
        }

        private async Task SaveDeliveryValue(BusinessScope scope, string table, string keyColumn, Guid id,
            Guid version, Guid? expectedVersion, string payload, CancellationToken ct)
        {
            RequireScope(scope);
            var values = new { Id = Text(id), Version = Text(version), Previous = Text(expectedVersion), Payload = payload };
            await Write(expectedVersion is null
                ? $"INSERT INTO {table} (TENANT_ID,ORGANIZATION_ID,{keyColumn},VERSION,PAYLOAD) VALUES (@TenantId,@OrganizationId,@Id,@Version,@Payload)"
                : $"UPDATE {table} SET VERSION=@Version,PAYLOAD=@Payload WHERE {ScopeWhere} AND {keyColumn}=@Id AND VERSION=@Previous",
                values, ct);
        }
    }
}
