using System.Data;
using NexaFramework.Service;
using NexaFramework.Service.Collaboration;

namespace NexaOne.ERP.Infrastructure;

public sealed partial class BillingBridge
{
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
