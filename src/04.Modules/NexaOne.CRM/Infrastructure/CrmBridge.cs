using System.Data;
using System.Data.Common;
using Dapper;
using NexaDB.Data.Abstractions.Models;
using NexaFramework.Service;
using NexaFramework.Service.Crm;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Crm;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.CRM.Infrastructure;

internal sealed class CrmBridge : ICrmBridge
{
    private const string ProductId = "NexaOne.MES";
    private readonly PipelineService _pipelines;
    private readonly DealService _deals;

    public CrmBridge(EesDataSource dataSource, IBusinessMembershipBridge memberships)
    {
        var adapter = new Adapter(dataSource, memberships);
        _pipelines = new PipelineService(adapter, adapter);
        _deals = new DealService(adapter, adapter);
    }

    public Task<CrmPage<Pipeline>> ListPipelinesAsync(string userId, Guid tenantId, Guid organizationId,
        PipelineQuery query, CancellationToken ct = default) => _pipelines.ListAsync(Actor(userId, tenantId, organizationId), query, ct);
    public Task<PipelineDetails> GetPipelineAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default) => _pipelines.GetAsync(Actor(userId, tenantId, organizationId), id, ct);
    public Task<PipelineDetails> CreatePipelineAsync(string userId, Guid tenantId, Guid organizationId,
        PipelineInput input, CancellationToken ct = default) => _pipelines.CreateAsync(Actor(userId, tenantId, organizationId), input, ct);
    public Task<PipelineDetails> UpdatePipelineAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, PipelineInput input, DealRemoval removal, CancellationToken ct = default)
        => _pipelines.UpdateAsync(Actor(userId, tenantId, organizationId), id, version, input, removal, ct);
    public Task DeletePipelineAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, DealRemoval removal, CancellationToken ct = default)
        => _pipelines.DeleteAsync(Actor(userId, tenantId, organizationId), id, version, removal, ct);
    public Task<CrmPage<Deal>> ListDealsAsync(string userId, Guid tenantId, Guid organizationId,
        DealQuery query, CancellationToken ct = default) => _deals.ListAsync(Actor(userId, tenantId, organizationId), query, ct);
    public Task<Deal> GetDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, CancellationToken ct = default) => _deals.GetAsync(Actor(userId, tenantId, organizationId), id, ct);
    public Task<Deal> CreateDealAsync(string userId, Guid tenantId, Guid organizationId,
        DealInput input, CancellationToken ct = default) => _deals.CreateAsync(Actor(userId, tenantId, organizationId), input, ct);
    public Task<Deal> UpdateDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, DealInput input, CancellationToken ct = default)
        => _deals.UpdateAsync(Actor(userId, tenantId, organizationId), id, version, input, ct);
    public Task<Deal> MoveDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, Guid stageId, CancellationToken ct = default)
        => _deals.MoveAsync(Actor(userId, tenantId, organizationId), id, version, stageId, ct);
    public Task DeleteDealAsync(string userId, Guid tenantId, Guid organizationId,
        Guid id, Guid version, CancellationToken ct = default)
        => _deals.DeleteAsync(Actor(userId, tenantId, organizationId), id, version, ct);

    private static BusinessActor Actor(string userId, Guid tenantId, Guid organizationId)
    {
        if (tenantId == Guid.Empty || organizationId == Guid.Empty)
            throw new BusinessException("INVALID_CRM_SCOPE");
        try
        {
            return new BusinessActor(userId,
                new BusinessScope(ProductId, tenantId.ToString("D"), organizationId.ToString("D")));
        }
        catch (ArgumentException)
        {
            throw new BusinessException("INVALID_CRM_SCOPE");
        }
    }

    private sealed class Adapter : ICrmStore, ICrmAuthorizer
    {
        private readonly ServiceObjectProcessor _processor;
        private readonly IBusinessMembershipBridge _memberships;
        private readonly DatabaseProviderKind _provider;
        private readonly int? _timeout;

        public Adapter(EesDataSource dataSource, IBusinessMembershipBridge memberships)
        {
            _processor = new ServiceObjectProcessor(dataSource);
            _memberships = memberships;
            _provider = dataSource.Provider.Kind;
            _timeout = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        }

        public async Task<bool> IsAllowedAsync(BusinessActor actor, CrmPermission permission,
            Guid? resourceId, CancellationToken ct)
        {
            var access = await Access(actor, ct).ConfigureAwait(false);
            if (access is null) return false;
            return permission switch
            {
                CrmPermission.Read => Has(access, CrmPermissions.Read),
                CrmPermission.ManagePipelines or CrmPermission.ManageStages => Has(access, CrmPermissions.ManagePipelines),
                CrmPermission.ManageDeals => Has(access, CrmPermissions.ManageDeals),
                CrmPermission.DeleteDeals => Has(access, CrmPermissions.DeleteDeals),
                _ => false,
            };
        }

        public async Task<DealAccess?> ResolveDealAccessAsync(BusinessActor actor,
            CrmPermission permission, CancellationToken ct)
        {
            var access = await Access(actor, ct).ConfigureAwait(false);
            if (access is null || !OperationAllowed(access, permission)) return null;
            if (Has(access, CrmPermissions.AllDeals)) return new(DealVisibility.All);
            if (Has(access, CrmPermissions.AssignedOrCreatedDeals))
                return new(DealVisibility.AssignedOrCreated, access.BusinessUserId);
            if (Has(access, CrmPermissions.AssignedDeals))
                return new(DealVisibility.Assigned, access.BusinessUserId);
            if (Has(access, CrmPermissions.CreatedDeals)) return new(DealVisibility.Created);
            return null;
        }

        public async Task<T> InTransactionAsync<T>(BusinessScope scope,
            Func<ICrmTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(scope);
            ArgumentNullException.ThrowIfNull(work);
            try
            {
                return await _processor.ExecuteInTransactionAsync(async (connection, transaction) =>
                {
                    var tx = new Transaction(connection, transaction, Scope(scope), _memberships, _provider, _timeout);
                    var result = await work(tx, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    return result;
                }, IsolationLevel.Serializable, ct).ConfigureAwait(false);
            }
            catch (BusinessException) { throw; }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw new BusinessStorageException(error); }
        }

        private async Task<BusinessMembership?> Access(BusinessActor actor, CancellationToken ct)
        {
            if (!Guid.TryParseExact(actor.Scope.TenantId, "D", out var tenantId)
                || !Guid.TryParseExact(actor.Scope.OrganizationId, "D", out var organizationId)) return null;
            return await _memberships.GetAccessAsync(actor.UserId, tenantId, organizationId, ct).ConfigureAwait(false);
        }

        private static bool OperationAllowed(BusinessMembership access, CrmPermission permission) => permission switch
        {
            CrmPermission.Read => Has(access, CrmPermissions.Read),
            CrmPermission.ManageDeals => Has(access, CrmPermissions.ManageDeals),
            CrmPermission.DeleteDeals => Has(access, CrmPermissions.DeleteDeals),
            _ => false,
        };
        private static bool Has(BusinessMembership access, string permission)
            => access.Permissions.Contains(permission, StringComparer.Ordinal);
        private static ScopeKey Scope(BusinessScope scope) => new(scope,
            Guid.ParseExact(scope.TenantId, "D").ToString("D"),
            Guid.ParseExact(scope.OrganizationId, "D").ToString("D"));
    }

    private sealed class Transaction : ICrmTransaction
    {
        private readonly DbConnection _connection;
        private readonly DbTransaction _transaction;
        private readonly ScopeKey _scope;
        private readonly IBusinessMembershipBridge _memberships;
        private readonly int? _timeout;
        private readonly string _pageSql;
        private readonly string _contains;

        public Transaction(DbConnection connection, DbTransaction transaction, ScopeKey scope,
            IBusinessMembershipBridge memberships, DatabaseProviderKind provider, int? timeout)
        {
            _connection = connection;
            _transaction = transaction;
            _scope = scope;
            _memberships = memberships;
            _timeout = timeout;
            _pageSql = provider == DatabaseProviderKind.SqlServer
                ? " OFFSET @Offset ROWS FETCH NEXT @Limit ROWS ONLY"
                : " LIMIT @Limit OFFSET @Offset";
            _contains = provider == DatabaseProviderKind.SqlServer ? "CHARINDEX({0}, {1}) > 0" : "INSTR({1}, {0}) > 0";
        }

        public async Task<Pipeline?> FindPipelineAsync(Guid id, CancellationToken ct)
        {
            var row = await _connection.QuerySingleOrDefaultAsync<PipelineRow>(Command("""
                SELECT PIPELINE_ID AS Id, VERSION AS Version, NAME AS Name, DESCRIPTION AS Description
                  FROM CRM_PIPELINE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@Id
                """, Key(id), ct));
            return row is null ? null : row.Value(_scope.Value);
        }

        public async Task<IReadOnlyList<PipelineStage>> ReadStagesAsync(Guid pipelineId, CancellationToken ct)
        {
            var rows = await _connection.QueryAsync<StageRow>(Command("""
                SELECT STAGE_ID AS Id, PIPELINE_ID AS PipelineId, NAME AS Name,
                       DESCRIPTION AS Description, STAGE_INDEX AS StageIndex
                  FROM CRM_PIPELINE_STAGE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@PipelineId
                 ORDER BY STAGE_INDEX, STAGE_ID
                """, Params(new { PipelineId = Id(pipelineId) }), ct));
            return Array.AsReadOnly(rows.Select(row => row.Value(_scope.Value)).ToArray());
        }

        public async Task<CrmPage<Pipeline>> QueryPipelinesAsync(PipelineQuery query, CancellationToken ct)
        {
            var where = " WHERE p.TENANT_ID=@TenantId AND p.ORGANIZATION_ID=@OrganizationId";
            if (query.Name is not null) where += " AND " + string.Format(_contains, "@Name", "p.NAME");
            if (query.Description is not null) where += " AND " + string.Format(_contains, "@Description", "COALESCE(p.DESCRIPTION,'')");
            if (query.StageName is not null) where += " AND EXISTS (SELECT 1 FROM CRM_PIPELINE_STAGE s WHERE s.TENANT_ID=p.TENANT_ID AND s.ORGANIZATION_ID=p.ORGANIZATION_ID AND s.PIPELINE_ID=p.PIPELINE_ID AND "
                + string.Format(_contains, "@StageName", "s.NAME") + ")";
            var values = Params(new { query.Offset, query.Limit, query.Name, query.Description, query.StageName });
            var total = await _connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*) FROM CRM_PIPELINE p" + where, values, ct));
            var rows = await _connection.QueryAsync<PipelineRow>(Command("""
                SELECT p.PIPELINE_ID AS Id, p.VERSION AS Version, p.NAME AS Name, p.DESCRIPTION AS Description
                  FROM CRM_PIPELINE p
                """ + where + " ORDER BY p.NAME, p.PIPELINE_ID" + _pageSql, values, ct));
            return new(Array.AsReadOnly(rows.Select(row => row.Value(_scope.Value)).ToArray()), total);
        }

        public async Task<bool> HasDealsAsync(IReadOnlyList<Guid> stageIds, CancellationToken ct)
        {
            if (stageIds.Count == 0) return false;
            return await _connection.ExecuteScalarAsync<int>(Command("""
                SELECT COUNT(*) FROM CRM_DEAL
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND STAGE_ID IN @StageIds
                """, Params(new { StageIds = stageIds.Select(Id).ToArray() }), ct)) > 0;
        }

        public async Task SavePipelineAsync(Pipeline pipeline, Guid? expectedVersion,
            IReadOnlyList<PipelineStage> stages, bool cascadeDeals, CancellationToken ct)
        {
            RequireScope(pipeline.Scope);
            var values = Params(new { Id = Id(pipeline.Id), Version = Id(pipeline.Version), pipeline.Name, pipeline.Description,
                ExpectedVersion = expectedVersion.HasValue ? Id(expectedVersion.Value) : null });
            if (expectedVersion is null)
            {
                await ExecuteOne("""
                    INSERT INTO CRM_PIPELINE (TENANT_ID,ORGANIZATION_ID,PIPELINE_ID,VERSION,NAME,DESCRIPTION)
                    VALUES (@TenantId,@OrganizationId,@Id,@Version,@Name,@Description)
                    """, values, "CRM_VERSION_CONFLICT", ct);
            }
            else
            {
                await ExecuteOne("""
                    UPDATE CRM_PIPELINE SET VERSION=@Version,NAME=@Name,DESCRIPTION=@Description
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                       AND PIPELINE_ID=@Id AND VERSION=@ExpectedVersion
                    """, values, "CRM_VERSION_CONFLICT", ct);
            }

            var current = (await _connection.QueryAsync<string>(Command("""
                SELECT STAGE_ID FROM CRM_PIPELINE_STAGE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@Id
                """, Params(new { Id = Id(pipeline.Id) }), ct))).ToHashSet(StringComparer.Ordinal);
            // Move existing positions out of the 1..100 range before applying a complete replacement.
            // This keeps reorder operations from tripping the unique pipeline/index key mid-update.
            if (current.Count > 0)
                await _connection.ExecuteAsync(Command("""
                    UPDATE CRM_PIPELINE_STAGE SET STAGE_INDEX=STAGE_INDEX+1000
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@Id
                    """, Params(new { Id = Id(pipeline.Id) }), ct));
            var retained = stages.Select(stage => Id(stage.Id)).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in current.Except(retained).ToArray())
            {
                if (cascadeDeals) await DeleteDealsForStage(removed, ct);
                else if (await HasDealsAsync([Guid.ParseExact(removed, "D")], ct)) throw new BusinessException("STAGE_HAS_DEALS");
                await _connection.ExecuteAsync(Command("""
                    DELETE FROM CRM_PIPELINE_STAGE
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@PipelineId AND STAGE_ID=@StageId
                    """, Params(new { PipelineId = Id(pipeline.Id), StageId = removed }), ct));
            }
            foreach (var stage in stages)
            {
                RequireScope(stage.Scope);
                var stageValues = Params(new { Id = Id(stage.Id), PipelineId = Id(pipeline.Id), stage.Name,
                    stage.Description, StageIndex = stage.Index });
                var owner = await _connection.QuerySingleOrDefaultAsync<string>(Command("""
                    SELECT PIPELINE_ID FROM CRM_PIPELINE_STAGE
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND STAGE_ID=@Id
                    """, stageValues, ct));
                if (owner is not null && owner != Id(pipeline.Id)) throw new BusinessException("CRM_STORAGE_CONTRACT_VIOLATION");
                if (owner is null)
                    await ExecuteOne("""
                        INSERT INTO CRM_PIPELINE_STAGE
                            (TENANT_ID,ORGANIZATION_ID,STAGE_ID,PIPELINE_ID,NAME,DESCRIPTION,STAGE_INDEX)
                        VALUES (@TenantId,@OrganizationId,@Id,@PipelineId,@Name,@Description,@StageIndex)
                        """, stageValues, "CRM_STORAGE_CONTRACT_VIOLATION", ct);
                else
                    await ExecuteOne("""
                        UPDATE CRM_PIPELINE_STAGE SET NAME=@Name,DESCRIPTION=@Description,STAGE_INDEX=@StageIndex
                         WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND STAGE_ID=@Id AND PIPELINE_ID=@PipelineId
                        """, stageValues, "CRM_STORAGE_CONTRACT_VIOLATION", ct);
            }
        }

        public async Task DeletePipelineAsync(Guid id, Guid expectedVersion, bool cascadeDeals, CancellationToken ct)
        {
            var stages = (await _connection.QueryAsync<string>(Command("""
                SELECT STAGE_ID FROM CRM_PIPELINE_STAGE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@Id
                """, Key(id), ct))).ToArray();
            if (!cascadeDeals && stages.Length > 0
                && await HasDealsAsync(stages.Select(value => Guid.ParseExact(value, "D")).ToArray(), ct))
                throw new BusinessException("STAGE_HAS_DEALS");
            if (cascadeDeals) foreach (var stage in stages) await DeleteDealsForStage(stage, ct);
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_PIPELINE_STAGE WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@Id
                """, Key(id), ct));
            await ExecuteOne("""
                DELETE FROM CRM_PIPELINE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PIPELINE_ID=@Id AND VERSION=@Version
                """, Params(new { Id = Id(id), Version = Id(expectedVersion) }), "CRM_VERSION_CONFLICT", ct);
        }

        public async Task<PipelineStage?> FindStageAsync(Guid id, CancellationToken ct)
        {
            var row = await _connection.QuerySingleOrDefaultAsync<StageRow>(Command("""
                SELECT STAGE_ID AS Id, PIPELINE_ID AS PipelineId, NAME AS Name,
                       DESCRIPTION AS Description, STAGE_INDEX AS StageIndex
                  FROM CRM_PIPELINE_STAGE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND STAGE_ID=@Id
                """, Key(id), ct));
            return row is null ? null : row.Value(_scope.Value);
        }

        public async Task<Deal?> FindDealAsync(Guid id, CancellationToken ct)
        {
            var row = await _connection.QuerySingleOrDefaultAsync<DealRow>(Command(DealSelect +
                " WHERE d.TENANT_ID=@TenantId AND d.ORGANIZATION_ID=@OrganizationId AND d.DEAL_ID=@Id", Key(id), ct));
            return row is null ? null : await Deal(row, ct);
        }

        public Task<CrmPage<Deal>> QueryDealsAsync(DealQuery query, CancellationToken ct)
            => QueryDealsAsync(query, new DealVisibilityFilter(true, null, null), ct);

        public async Task<CrmPage<Deal>> QueryDealsAsync(DealQuery query, DealVisibilityFilter visibility, CancellationToken ct)
        {
            var where = " WHERE d.TENANT_ID=@TenantId AND d.ORGANIZATION_ID=@OrganizationId";
            if (query.PipelineId.HasValue) where += " AND s.PIPELINE_ID=@PipelineId";
            if (query.StageId.HasValue) where += " AND d.STAGE_ID=@StageId";
            if (query.ClientId.HasValue) where += " AND d.CLIENT_ID=@ClientId";
            if (!visibility.All)
            {
                where += " AND (1=0";
                if (visibility.EmployeeId.HasValue)
                    where += " OR EXISTS (SELECT 1 FROM CRM_DEAL_ASSIGNEE a WHERE a.TENANT_ID=d.TENANT_ID AND a.ORGANIZATION_ID=d.ORGANIZATION_ID AND a.DEAL_ID=d.DEAL_ID AND a.EMPLOYEE_ID=@EmployeeId)";
                if (visibility.CreatedByUserId is not null) where += " OR d.CREATED_BY=@CreatedBy";
                where += ")";
            }
            var values = Params(new
            {
                query.Offset, query.Limit,
                PipelineId = query.PipelineId.HasValue ? Id(query.PipelineId.Value) : null,
                StageId = query.StageId.HasValue ? Id(query.StageId.Value) : null,
                ClientId = query.ClientId.HasValue ? Id(query.ClientId.Value) : null,
                EmployeeId = visibility.EmployeeId.HasValue ? Id(visibility.EmployeeId.Value) : null,
                CreatedBy = visibility.CreatedByUserId,
            });
            var from = " FROM CRM_DEAL d JOIN CRM_PIPELINE_STAGE s ON s.TENANT_ID=d.TENANT_ID AND s.ORGANIZATION_ID=d.ORGANIZATION_ID AND s.STAGE_ID=d.STAGE_ID";
            var total = await _connection.ExecuteScalarAsync<int>(Command("SELECT COUNT(*)" + from + where, values, ct));
            var rows = (await _connection.QueryAsync<DealRow>(Command(DealSelect + where
                + " ORDER BY s.PIPELINE_ID,s.STAGE_INDEX,d.DEAL_ID" + _pageSql, values, ct))).ToArray();
            var items = new Deal[rows.Length];
            for (var index = 0; index < rows.Length; index++) items[index] = await Deal(rows[index], ct);
            return new(Array.AsReadOnly(items), total);
        }

        public async Task<IReadOnlyList<Guid>> ExistingDealEmployeesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        {
            var found = new List<Guid>(ids.Count);
            foreach (var id in ids)
            {
                var member = await _memberships.GetActiveMemberInTransactionAsync(_transaction,
                    Guid.ParseExact(_scope.TenantId, "D"), Guid.ParseExact(_scope.OrganizationId, "D"), id, ct);
                if (member is not null) found.Add(id);
            }
            return found.AsReadOnly();
        }

        public Task<bool> ClientExistsAsync(Guid clientId, CancellationToken ct) => Task.FromResult(false);

        public async Task<bool> ClientIsLinkedAsync(Guid clientId, Guid? exceptDealId, CancellationToken ct)
            => await _connection.ExecuteScalarAsync<int>(Command("""
                SELECT COUNT(*) FROM CRM_DEAL
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND CLIENT_ID=@ClientId
                   AND (@ExceptId IS NULL OR DEAL_ID<>@ExceptId)
                """, Params(new { ClientId = Id(clientId), ExceptId = exceptDealId.HasValue ? Id(exceptDealId.Value) : null }), ct)) > 0;

        public async Task SaveDealAsync(Deal deal, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(deal.Scope);
            var values = Params(new { Id = Id(deal.Id), Version = Id(deal.Version), StageId = Id(deal.StageId),
                deal.Title, deal.Probability, ClientId = deal.ClientId.HasValue ? Id(deal.ClientId.Value) : null,
                deal.CreatedByUserId, ExpectedVersion = expectedVersion.HasValue ? Id(expectedVersion.Value) : null });
            if (expectedVersion is null)
                await ExecuteOne("""
                    INSERT INTO CRM_DEAL
                        (TENANT_ID,ORGANIZATION_ID,DEAL_ID,VERSION,STAGE_ID,TITLE,PROBABILITY,CLIENT_ID,CREATED_BY)
                    VALUES (@TenantId,@OrganizationId,@Id,@Version,@StageId,@Title,@Probability,@ClientId,@CreatedByUserId)
                    """, values, "CRM_VERSION_CONFLICT", ct);
            else
                await ExecuteOne("""
                    UPDATE CRM_DEAL SET VERSION=@Version,STAGE_ID=@StageId,TITLE=@Title,
                           PROBABILITY=@Probability,CLIENT_ID=@ClientId
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                       AND DEAL_ID=@Id AND VERSION=@ExpectedVersion
                    """, values, "CRM_VERSION_CONFLICT", ct);
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_DEAL_ASSIGNEE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND DEAL_ID=@Id
                """, values, ct));
            foreach (var employeeId in deal.AssignedEmployeeIds)
                await ExecuteOne("""
                    INSERT INTO CRM_DEAL_ASSIGNEE (TENANT_ID,ORGANIZATION_ID,DEAL_ID,EMPLOYEE_ID)
                    VALUES (@TenantId,@OrganizationId,@Id,@EmployeeId)
                    """, Params(new { Id = Id(deal.Id), EmployeeId = Id(employeeId) }), "CRM_STORAGE_CONTRACT_VIOLATION", ct);
        }

        public async Task DeleteDealAsync(Guid id, Guid expectedVersion, CancellationToken ct)
        {
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_DEAL_ASSIGNEE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND DEAL_ID=@Id
                """, Key(id), ct));
            await ExecuteOne("""
                DELETE FROM CRM_DEAL
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND DEAL_ID=@Id AND VERSION=@Version
                """, Params(new { Id = Id(id), Version = Id(expectedVersion) }), "CRM_VERSION_CONFLICT", ct);
        }

        public async Task AppendAuditAsync(CrmAudit audit, CancellationToken ct)
        {
            RequireScope(audit.Actor.Scope);
            await ExecuteOne("""
                INSERT INTO CRM_AUDIT
                    (TENANT_ID,ORGANIZATION_ID,CHANGE_ID,ACTOR_USER_ID,OPERATION,ENTITY_ID,
                     PREVIOUS_VERSION,VERSION,RECORDED_AT_TICKS)
                VALUES (@TenantId,@OrganizationId,@ChangeId,@ActorUserId,@Operation,@EntityId,
                        @PreviousVersion,@Version,@RecordedAtTicks)
                """, Params(new { ChangeId = Id(audit.ChangeId), ActorUserId = audit.Actor.UserId, audit.Operation,
                    EntityId = Id(audit.EntityId), PreviousVersion = audit.PreviousVersion.HasValue ? Id(audit.PreviousVersion.Value) : null,
                    Version = audit.Version.HasValue ? Id(audit.Version.Value) : null,
                    RecordedAtTicks = audit.RecordedAt.UtcTicks }), "CRM_STORAGE_CONTRACT_VIOLATION", ct);
        }

        private const string DealSelect = """
            SELECT d.DEAL_ID AS Id,d.VERSION AS Version,d.STAGE_ID AS StageId,d.TITLE AS Title,
                   d.PROBABILITY AS Probability,d.CLIENT_ID AS ClientId,d.CREATED_BY AS CreatedByUserId
              FROM CRM_DEAL d
              JOIN CRM_PIPELINE_STAGE s ON s.TENANT_ID=d.TENANT_ID AND s.ORGANIZATION_ID=d.ORGANIZATION_ID AND s.STAGE_ID=d.STAGE_ID
            """;

        private async Task<Deal> Deal(DealRow row, CancellationToken ct)
        {
            var assigned = await _connection.QueryAsync<string>(Command("""
                SELECT EMPLOYEE_ID FROM CRM_DEAL_ASSIGNEE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND DEAL_ID=@Id
                 ORDER BY EMPLOYEE_ID
                """, Params(new { row.Id }), ct));
            return row.Value(_scope.Value, assigned.Select(value => Guid.ParseExact(value, "D")).ToArray());
        }

        private async Task DeleteDealsForStage(string stageId, CancellationToken ct)
        {
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_DEAL_ASSIGNEE
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId
                   AND DEAL_ID IN (SELECT DEAL_ID FROM CRM_DEAL WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND STAGE_ID=@StageId)
                """, Params(new { StageId = stageId }), ct));
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_DEAL WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND STAGE_ID=@StageId
                """, Params(new { StageId = stageId }), ct));
        }

        private async Task ExecuteOne(string sql, object values, string errorCode, CancellationToken ct)
        {
            if (await _connection.ExecuteAsync(Command(sql, values, ct)) != 1) throw new BusinessException(errorCode);
        }
        private CommandDefinition Command(string sql, object values, CancellationToken ct)
            => new(sql, values, _transaction, commandTimeout: _timeout, cancellationToken: ct);
        private object Key(Guid id) => Params(new { Id = Id(id) });
        private DynamicParameters Params(object? values = null)
        {
            var parameters = new DynamicParameters(values);
            parameters.Add("TenantId", _scope.TenantId);
            parameters.Add("OrganizationId", _scope.OrganizationId);
            return parameters;
        }
        private void RequireScope(BusinessScope scope)
        {
            if (scope != _scope.Value) throw new BusinessException("CRM_STORAGE_CONTRACT_VIOLATION");
        }
        private static string Id(Guid id) => id.ToString("D");
    }

    private sealed record ScopeKey(BusinessScope Value, string TenantId, string OrganizationId);
    private sealed class PipelineRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public Pipeline Value(BusinessScope scope) => new(Guid.ParseExact(Id, "D"), scope,
            Guid.ParseExact(Version, "D"), Name, Description);
    }
    private sealed class StageRow
    {
        public string Id { get; set; } = "";
        public string PipelineId { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public int StageIndex { get; set; }
        public PipelineStage Value(BusinessScope scope) => new(Guid.ParseExact(Id, "D"), scope,
            Guid.ParseExact(PipelineId, "D"), Name, Description, StageIndex);
    }
    private sealed class DealRow
    {
        public string Id { get; set; } = "";
        public string Version { get; set; } = "";
        public string StageId { get; set; } = "";
        public string Title { get; set; } = "";
        public int Probability { get; set; }
        public string? ClientId { get; set; }
        public string? CreatedByUserId { get; set; }
        public Deal Value(BusinessScope scope, IReadOnlyList<Guid> assigned) => new(
            Guid.ParseExact(Id, "D"), scope, Guid.ParseExact(Version, "D"), Guid.ParseExact(StageId, "D"),
            Title, Probability, ClientId is null ? null : Guid.ParseExact(ClientId, "D"))
        { CreatedByUserId = CreatedByUserId, AssignedEmployeeIds = assigned };
    }
}
