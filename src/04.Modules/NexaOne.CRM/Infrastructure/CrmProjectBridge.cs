using System.Data;
using System.Data.Common;
using Dapper;
using NexaDB.Data.Abstractions.Models;
using NexaFramework.Service;
using NexaFramework.Service.Projects;
using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.CRM.Infrastructure;

/// <summary>Product adapter for Framework project and team services.</summary>
internal sealed class CrmProjectBridge
{
    private readonly ProjectService _projects;
    private readonly TeamService _teams;

    public CrmProjectBridge(EesDataSource dataSource, IBusinessMembershipBridge memberships)
    {
        var adapter = new Adapter(dataSource, memberships);
        _projects = new ProjectService(adapter, adapter);
        _teams = new TeamService(adapter, adapter);
    }

    public Task<WorkPage<Project>> ListProjectsAsync(BusinessActor actor, ProjectQuery query, CancellationToken ct)
        => _projects.ListAsync(actor, query, ct);
    public Task<Project> GetProjectAsync(BusinessActor actor, Guid id, CancellationToken ct)
        => _projects.GetAsync(actor, id, ct);
    public Task<Project> CreateProjectAsync(BusinessActor actor, ProjectInput input, ProjectLinks links, CancellationToken ct)
        => _projects.CreateAsync(actor, input, links, ct);
    public Task<Project> UpdateProjectAsync(BusinessActor actor, Guid id, Guid version, ProjectInput input, CancellationToken ct)
        => _projects.UpdateAsync(actor, id, version, input, ct);
    public Task<Project> SetProjectLinksAsync(BusinessActor actor, Guid id, Guid version, ProjectLinks links, CancellationToken ct)
        => _projects.SetLinksAsync(actor, id, version, links, ct);
    public Task DeleteProjectAsync(BusinessActor actor, Guid id, Guid version, CancellationToken ct)
        => _projects.DeleteAsync(actor, id, version, ct);
    public Task<WorkPage<Team>> ListTeamsAsync(BusinessActor actor, TeamQuery query, CancellationToken ct)
        => _teams.ListAsync(actor, query, ct);
    public Task<Team> GetTeamAsync(BusinessActor actor, Guid id, CancellationToken ct)
        => _teams.GetAsync(actor, id, ct);
    public Task<Team> CreateTeamAsync(BusinessActor actor, TeamInput input, IReadOnlyList<ProjectMember> members, CancellationToken ct)
        => _teams.CreateAsync(actor, input, members, ct);
    public Task<Team> UpdateTeamAsync(BusinessActor actor, Guid id, Guid version, TeamInput input,
        IReadOnlyList<ProjectMember>? members, CancellationToken ct)
        => _teams.UpdateAsync(actor, id, version, input, members, ct);
    public Task DeleteTeamAsync(BusinessActor actor, Guid id, Guid version, CancellationToken ct)
        => _teams.DeleteAsync(actor, id, version, ct);

    private sealed class Adapter : IWorkStore, IWorkAuthorizer
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

        public async Task<bool> IsAllowedAsync(BusinessActor actor, WorkPermission permission,
            Guid? resourceId, CancellationToken ct)
        {
            var access = await Access(actor, ct).ConfigureAwait(false);
            if (access is null) return false;
            return permission switch
            {
                WorkPermission.ReadProjects => Has(access, CrmPermissions.ReadProjects),
                WorkPermission.ManageProjects => Has(access, CrmPermissions.ManageProjects),
                WorkPermission.DeleteProjects => Has(access, CrmPermissions.DeleteProjects),
                WorkPermission.ReadTeams => Has(access, CrmPermissions.ReadTeams),
                WorkPermission.ManageTeams => Has(access, CrmPermissions.ManageTeams),
                WorkPermission.DeleteTeams => Has(access, CrmPermissions.DeleteTeams),
                WorkPermission.LinkCustomer => Has(access, CrmPermissions.LinkCustomer),
                _ => false,
            };
        }

        public async Task<ProjectAccess?> ResolveProjectAccessAsync(BusinessActor actor,
            WorkPermission permission, CancellationToken ct)
        {
            var access = await Access(actor, ct).ConfigureAwait(false);
            if (access is null || !ProjectOperationAllowed(access, permission)) return null;
            if (Has(access, CrmPermissions.AllProjects)) return new(ProjectVisibility.All);
            if (Has(access, CrmPermissions.AssignedOrCreatedProjects))
                return new(ProjectVisibility.AssignedOrCreated, access.BusinessUserId);
            if (Has(access, CrmPermissions.AssignedProjects))
                return new(ProjectVisibility.Assigned, access.BusinessUserId);
            if (Has(access, CrmPermissions.CreatedProjects)) return new(ProjectVisibility.Created);
            return null;
        }

        public async Task<T> InTransactionAsync<T>(BusinessScope scope,
            Func<IWorkTransaction, CancellationToken, Task<T>> work, CancellationToken ct)
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

        private static bool ProjectOperationAllowed(BusinessMembership access, WorkPermission permission) => permission switch
        {
            WorkPermission.ReadProjects => Has(access, CrmPermissions.ReadProjects),
            WorkPermission.ManageProjects => Has(access, CrmPermissions.ManageProjects),
            WorkPermission.DeleteProjects => Has(access, CrmPermissions.DeleteProjects),
            _ => false,
        };
        private static bool Has(BusinessMembership access, string permission)
            => access.Permissions.Contains(permission, StringComparer.Ordinal);
        private static ScopeKey Scope(BusinessScope scope) => new(scope,
            Guid.ParseExact(scope.TenantId, "D").ToString("D"),
            Guid.ParseExact(scope.OrganizationId, "D").ToString("D"));
    }

    private sealed class Transaction : IWorkTransaction
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

        public async Task<IReadOnlyList<Guid>> ExistingEmployeesAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
        {
            var found = new List<Guid>(ids.Count);
            foreach (var id in ids)
            {
                var member = await _memberships.GetActiveMemberInTransactionAsync(_transaction,
                    Guid.ParseExact(_scope.TenantId, "D"), Guid.ParseExact(_scope.OrganizationId, "D"),
                    id, ct).ConfigureAwait(false);
                if (member is not null) found.Add(id);
            }
            return Array.AsReadOnly(found.ToArray());
        }

        public async Task<bool> CustomerExistsAsync(Guid id, CancellationToken ct)
            => await _connection.ExecuteScalarAsync<int>(Command("""
                SELECT COUNT(*) FROM CRM_CUSTOMER_ENROLLMENT
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND CONTACT_ID=@Id
                """, Key(id), ct)) == 1;

        public async Task<Project?> FindProjectAsync(Guid id, CancellationToken ct)
        {
            var row = await _connection.QuerySingleOrDefaultAsync<ProjectRow>(Command("""
                SELECT PROJECT_ID AS Id, VERSION AS Version, CREATED_BY AS CreatedBy,
                       NAME AS Name, DESCRIPTION AS Description, CODE AS Code,
                       PROJECT_STATUS AS Status, START_AT_TICKS AS StartTicks, END_AT_TICKS AS EndTicks,
                       IS_BILLABLE AS Billable, IS_PUBLIC AS IsPublic, BUDGET AS Budget,
                       BUDGET_TYPE AS BudgetType, CUSTOMER_ID AS CustomerId
                  FROM CRM_PROJECT
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id
                """, Key(id), ct));
            return row is null ? null : await ProjectValue(row, ct).ConfigureAwait(false);
        }

        public async Task<Team?> FindTeamAsync(Guid id, CancellationToken ct)
        {
            var row = await _connection.QuerySingleOrDefaultAsync<TeamRow>(Command("""
                SELECT TEAM_ID AS Id, VERSION AS Version, CREATED_BY AS CreatedBy,
                       NAME AS Name, TEAM_PREFIX AS Prefix, IS_PUBLIC AS IsPublic,
                       SHARE_PROFILE_VIEW AS ShareProfileView, REQUIRE_PLAN_TO_TRACK AS RequirePlanToTrack
                  FROM CRM_TEAM
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@Id
                """, Key(id), ct));
            return row is null ? null : await TeamValue(row, ct).ConfigureAwait(false);
        }

        public Task<WorkItem?> FindTaskAsync(Guid id, CancellationToken ct) => Task.FromResult<WorkItem?>(null);

        public Task<WorkPage<Project>> QueryProjectsAsync(ProjectQuery query, CancellationToken ct)
            => QueryProjectsAsync(query, new ProjectVisibilityFilter(true, null, null), ct);

        public async Task<WorkPage<Project>> QueryProjectsAsync(ProjectQuery query,
            ProjectVisibilityFilter visibility, CancellationToken ct)
        {
            var where = " WHERE p.TENANT_ID=@TenantId AND p.ORGANIZATION_ID=@OrganizationId";
            if (query.Name is not null) where += " AND " + string.Format(_contains, "@Name", "p.NAME");
            if (query.Status.HasValue) where += " AND p.PROJECT_STATUS=@Status";
            if (query.CustomerId.HasValue) where += " AND p.CUSTOMER_ID=@CustomerId";
            if (query.EmployeeId.HasValue) where += " AND EXISTS (SELECT 1 FROM CRM_PROJECT_MEMBER pm WHERE pm.TENANT_ID=p.TENANT_ID AND pm.ORGANIZATION_ID=p.ORGANIZATION_ID AND pm.PROJECT_ID=p.PROJECT_ID AND pm.EMPLOYEE_ID=@EmployeeId)";
            if (query.TeamId.HasValue) where += " AND EXISTS (SELECT 1 FROM CRM_PROJECT_TEAM pt WHERE pt.TENANT_ID=p.TENANT_ID AND pt.ORGANIZATION_ID=p.ORGANIZATION_ID AND pt.PROJECT_ID=p.PROJECT_ID AND pt.TEAM_ID=@TeamId)";
            if (!visibility.All) where += " AND ((@VisibleEmployeeId IS NOT NULL AND EXISTS (SELECT 1 FROM CRM_PROJECT_MEMBER vm WHERE vm.TENANT_ID=p.TENANT_ID AND vm.ORGANIZATION_ID=p.ORGANIZATION_ID AND vm.PROJECT_ID=p.PROJECT_ID AND vm.EMPLOYEE_ID=@VisibleEmployeeId)) OR (@VisibleCreator IS NOT NULL AND p.CREATED_BY=@VisibleCreator))";
            var values = Params(new
            {
                query.Offset,
                query.Limit,
                query.Name,
                Status = query.Status?.ToString(),
                CustomerId = OptionalId(query.CustomerId),
                EmployeeId = OptionalId(query.EmployeeId),
                TeamId = OptionalId(query.TeamId),
                VisibleEmployeeId = OptionalId(visibility.EmployeeId),
                VisibleCreator = visibility.CreatedByUserId,
            });
            var total = await _connection.ExecuteScalarAsync<long>(Command(
                "SELECT COUNT(*) FROM CRM_PROJECT p" + where, values, ct));
            var rows = (await _connection.QueryAsync<ProjectRow>(Command("""
                SELECT p.PROJECT_ID AS Id, p.VERSION AS Version, p.CREATED_BY AS CreatedBy,
                       p.NAME AS Name, p.DESCRIPTION AS Description, p.CODE AS Code,
                       p.PROJECT_STATUS AS Status, p.START_AT_TICKS AS StartTicks, p.END_AT_TICKS AS EndTicks,
                       p.IS_BILLABLE AS Billable, p.IS_PUBLIC AS IsPublic, p.BUDGET AS Budget,
                       p.BUDGET_TYPE AS BudgetType, p.CUSTOMER_ID AS CustomerId
                  FROM CRM_PROJECT p
                """ + where + " ORDER BY p.NAME,p.PROJECT_ID" + _pageSql, values, ct))).ToArray();
            var projects = new Project[rows.Length];
            for (var i = 0; i < rows.Length; i++) projects[i] = await ProjectValue(rows[i], ct).ConfigureAwait(false);
            return new(Array.AsReadOnly(projects), total);
        }

        public async Task<WorkPage<Team>> QueryTeamsAsync(TeamQuery query, CancellationToken ct)
        {
            var where = " WHERE t.TENANT_ID=@TenantId AND t.ORGANIZATION_ID=@OrganizationId";
            if (query.Name is not null) where += " AND " + string.Format(_contains, "@Name", "t.NAME");
            if (query.EmployeeId.HasValue) where += " AND EXISTS (SELECT 1 FROM CRM_TEAM_MEMBER tm WHERE tm.TENANT_ID=t.TENANT_ID AND tm.ORGANIZATION_ID=t.ORGANIZATION_ID AND tm.TEAM_ID=t.TEAM_ID AND tm.EMPLOYEE_ID=@EmployeeId)";
            var values = Params(new { query.Offset, query.Limit, query.Name, EmployeeId = OptionalId(query.EmployeeId) });
            var total = await _connection.ExecuteScalarAsync<long>(Command(
                "SELECT COUNT(*) FROM CRM_TEAM t" + where, values, ct));
            var rows = (await _connection.QueryAsync<TeamRow>(Command("""
                SELECT t.TEAM_ID AS Id, t.VERSION AS Version, t.CREATED_BY AS CreatedBy,
                       t.NAME AS Name, t.TEAM_PREFIX AS Prefix, t.IS_PUBLIC AS IsPublic,
                       t.SHARE_PROFILE_VIEW AS ShareProfileView, t.REQUIRE_PLAN_TO_TRACK AS RequirePlanToTrack
                  FROM CRM_TEAM t
                """ + where + " ORDER BY t.NAME,t.TEAM_ID" + _pageSql, values, ct))).ToArray();
            var teams = new Team[rows.Length];
            for (var i = 0; i < rows.Length; i++) teams[i] = await TeamValue(rows[i], ct).ConfigureAwait(false);
            return new(Array.AsReadOnly(teams), total);
        }

        public Task<WorkPage<WorkItem>> QueryTasksAsync(WorkItemQuery query, CancellationToken ct)
            => Task.FromResult(new WorkPage<WorkItem>(Array.Empty<WorkItem>(), 0));
        public Task<long> ReserveTaskNumberAsync(Guid? projectId, CancellationToken ct)
            => Unsupported<long>();

        public async Task SaveProjectAsync(Project project, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(project.Scope);
            var values = Params(new
            {
                Id = Id(project.Id),
                Version = Id(project.Version),
                project.CreatedByUserId,
                project.Values.Name,
                project.Values.Description,
                project.Values.Code,
                Status = project.Values.Status.ToString(),
                StartTicks = Ticks(project.Values.StartDate),
                EndTicks = Ticks(project.Values.EndDate),
                project.Values.Billable,
                IsPublic = project.Values.Public,
                project.Values.Budget,
                BudgetType = project.Values.BudgetType.ToString(),
                CustomerId = OptionalId(project.Links.CustomerId),
                ExpectedVersion = expectedVersion.HasValue ? Id(expectedVersion.Value) : null,
            });
            if (expectedVersion is null)
            {
                await ExecuteOne("""
                    INSERT INTO CRM_PROJECT
                        (TENANT_ID,ORGANIZATION_ID,PROJECT_ID,VERSION,CREATED_BY,NAME,DESCRIPTION,CODE,
                         PROJECT_STATUS,START_AT_TICKS,END_AT_TICKS,IS_BILLABLE,IS_PUBLIC,BUDGET,BUDGET_TYPE,CUSTOMER_ID)
                    VALUES (@TenantId,@OrganizationId,@Id,@Version,@CreatedByUserId,@Name,@Description,@Code,
                            @Status,@StartTicks,@EndTicks,@Billable,@IsPublic,@Budget,@BudgetType,@CustomerId)
                    """, values, "WORK_VERSION_CONFLICT", ct);
            }
            else
            {
                await ExecuteOne("""
                    UPDATE CRM_PROJECT
                       SET VERSION=@Version,NAME=@Name,DESCRIPTION=@Description,CODE=@Code,
                           PROJECT_STATUS=@Status,START_AT_TICKS=@StartTicks,END_AT_TICKS=@EndTicks,
                           IS_BILLABLE=@Billable,IS_PUBLIC=@IsPublic,BUDGET=@Budget,
                           BUDGET_TYPE=@BudgetType,CUSTOMER_ID=@CustomerId
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id
                       AND VERSION=@ExpectedVersion AND CREATED_BY=@CreatedByUserId
                    """, values, "WORK_VERSION_CONFLICT", ct);
            }
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_PROJECT_MEMBER WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id;
                DELETE FROM CRM_PROJECT_TEAM WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id;
                """, values, ct));
            foreach (var member in project.Links.Members)
                await ExecuteOne("""
                    INSERT INTO CRM_PROJECT_MEMBER (TENANT_ID,ORGANIZATION_ID,PROJECT_ID,EMPLOYEE_ID,IS_MANAGER)
                    VALUES (@TenantId,@OrganizationId,@ProjectId,@EmployeeId,@IsManager)
                    """, Params(new { ProjectId = Id(project.Id), EmployeeId = Id(member.EmployeeId), member.IsManager }),
                    "WORK_STORAGE_CONTRACT_VIOLATION", ct);
            foreach (var teamId in project.Links.TeamIds)
                await ExecuteOne("""
                    INSERT INTO CRM_PROJECT_TEAM (TENANT_ID,ORGANIZATION_ID,PROJECT_ID,TEAM_ID)
                    SELECT @TenantId,@OrganizationId,@ProjectId,@TeamId
                     WHERE EXISTS (SELECT 1 FROM CRM_TEAM WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@TeamId)
                    """, Params(new { ProjectId = Id(project.Id), TeamId = Id(teamId) }),
                    "TEAM_NOT_FOUND", ct);
        }

        public async Task SaveTeamAsync(Team team, Guid? expectedVersion, CancellationToken ct)
        {
            RequireScope(team.Scope);
            var values = Params(new
            {
                Id = Id(team.Id),
                Version = Id(team.Version),
                team.CreatedByUserId,
                team.Values.Name,
                team.Values.Prefix,
                IsPublic = team.Values.Public,
                team.Values.ShareProfileView,
                team.Values.RequirePlanToTrack,
                ExpectedVersion = expectedVersion.HasValue ? Id(expectedVersion.Value) : null,
            });
            if (expectedVersion is null)
                await ExecuteOne("""
                    INSERT INTO CRM_TEAM
                        (TENANT_ID,ORGANIZATION_ID,TEAM_ID,VERSION,CREATED_BY,NAME,TEAM_PREFIX,
                         IS_PUBLIC,SHARE_PROFILE_VIEW,REQUIRE_PLAN_TO_TRACK)
                    VALUES (@TenantId,@OrganizationId,@Id,@Version,@CreatedByUserId,@Name,@Prefix,
                            @IsPublic,@ShareProfileView,@RequirePlanToTrack)
                    """, values, "WORK_VERSION_CONFLICT", ct);
            else
                await ExecuteOne("""
                    UPDATE CRM_TEAM
                       SET VERSION=@Version,NAME=@Name,TEAM_PREFIX=@Prefix,IS_PUBLIC=@IsPublic,
                           SHARE_PROFILE_VIEW=@ShareProfileView,REQUIRE_PLAN_TO_TRACK=@RequirePlanToTrack
                     WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@Id
                       AND VERSION=@ExpectedVersion AND CREATED_BY=@CreatedByUserId
                    """, values, "WORK_VERSION_CONFLICT", ct);
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_TEAM_MEMBER WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@Id
                """, values, ct));
            foreach (var member in team.Members)
                await ExecuteOne("""
                    INSERT INTO CRM_TEAM_MEMBER (TENANT_ID,ORGANIZATION_ID,TEAM_ID,EMPLOYEE_ID,IS_MANAGER)
                    VALUES (@TenantId,@OrganizationId,@TeamId,@EmployeeId,@IsManager)
                    """, Params(new { TeamId = Id(team.Id), EmployeeId = Id(member.EmployeeId), member.IsManager }),
                    "WORK_STORAGE_CONTRACT_VIOLATION", ct);
        }

        public Task SaveTaskAsync(WorkItem task, Guid? expectedVersion, CancellationToken ct) => Unsupported();
        public Task<bool> HasProjectReferencesAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
        public async Task<bool> HasTeamReferencesAsync(Guid id, CancellationToken ct)
            => await _connection.ExecuteScalarAsync<int>(Command("""
                SELECT COUNT(*) FROM CRM_PROJECT_TEAM
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@Id
                """, Key(id), ct)) > 0;
        public Task<bool> HasTaskReferencesAsync(Guid id, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> HasTaskChildrenAsync(Guid id, CancellationToken ct) => Task.FromResult(false);

        public async Task DeleteProjectAsync(Guid id, Guid expectedVersion, CancellationToken ct)
        {
            var values = Params(new { Id = Id(id), ExpectedVersion = Id(expectedVersion) });
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_PROJECT_MEMBER WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id;
                DELETE FROM CRM_PROJECT_TEAM WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id;
                """, values, ct));
            await ExecuteOne("""
                DELETE FROM CRM_PROJECT
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id AND VERSION=@ExpectedVersion
                """, values, "WORK_VERSION_CONFLICT", ct);
        }

        public async Task DeleteTeamAsync(Guid id, Guid expectedVersion, CancellationToken ct)
        {
            var values = Params(new { Id = Id(id), ExpectedVersion = Id(expectedVersion) });
            await _connection.ExecuteAsync(Command("""
                DELETE FROM CRM_TEAM_MEMBER WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@Id
                """, values, ct));
            await ExecuteOne("""
                DELETE FROM CRM_TEAM
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@Id AND VERSION=@ExpectedVersion
                """, values, "WORK_VERSION_CONFLICT", ct);
        }

        public Task DeleteTaskAsync(Guid id, Guid expectedVersion, CancellationToken ct) => Unsupported();

        public Task AppendAuditAsync(WorkAudit entry, CancellationToken ct)
        {
            RequireScope(entry.Actor.Scope);
            return ExecuteOne("""
                INSERT INTO CRM_AUDIT
                    (TENANT_ID,ORGANIZATION_ID,CHANGE_ID,ACTOR_USER_ID,OPERATION,ENTITY_ID,
                     PREVIOUS_VERSION,VERSION,RECORDED_AT_TICKS)
                VALUES (@TenantId,@OrganizationId,@Id,@Actor,@Operation,@EntityId,
                        @PreviousVersion,@Version,@RecordedAt)
                """, Params(new
                {
                    Id = Id(entry.Id),
                    Actor = entry.Actor.UserId,
                    entry.Operation,
                    EntityId = Id(entry.ResourceId),
                    PreviousVersion = OptionalId(entry.PreviousVersion),
                    Version = OptionalId(entry.Version),
                    RecordedAt = entry.RecordedAt.UtcDateTime.Ticks,
                }), "WORK_STORAGE_CONTRACT_VIOLATION", ct);
        }

        private async Task<Project> ProjectValue(ProjectRow row, CancellationToken ct)
        {
            if (!Enum.TryParse<ProjectStatus>(row.Status, false, out var status)
                || !Enum.TryParse<ProjectBudgetType>(row.BudgetType, false, out var budgetType))
                throw new BusinessException("WORK_STORAGE_CONTRACT_VIOLATION");
            var projectId = Parse(row.Id);
            var members = (await _connection.QueryAsync<MemberRow>(Command("""
                SELECT EMPLOYEE_ID AS EmployeeId, IS_MANAGER AS IsManager
                  FROM CRM_PROJECT_MEMBER
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id
                 ORDER BY EMPLOYEE_ID
                """, Key(projectId), ct))).Select(value => new ProjectMember(Parse(value.EmployeeId), value.IsManager)).ToArray();
            var teams = (await _connection.QueryAsync<string>(Command("""
                SELECT TEAM_ID FROM CRM_PROJECT_TEAM
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND PROJECT_ID=@Id
                 ORDER BY TEAM_ID
                """, Key(projectId), ct))).Select(Parse).ToArray();
            return new(projectId, _scope.Value, Parse(row.Version), row.CreatedBy,
                new ProjectInput(row.Name, row.Description, row.Code, status, At(row.StartTicks), At(row.EndTicks),
                    row.Billable, row.IsPublic, row.Budget, budgetType),
                new ProjectLinks(OptionalParse(row.CustomerId), Array.AsReadOnly(members), Array.AsReadOnly(teams)));
        }

        private async Task<Team> TeamValue(TeamRow row, CancellationToken ct)
        {
            var teamId = Parse(row.Id);
            var members = (await _connection.QueryAsync<MemberRow>(Command("""
                SELECT EMPLOYEE_ID AS EmployeeId, IS_MANAGER AS IsManager
                  FROM CRM_TEAM_MEMBER
                 WHERE TENANT_ID=@TenantId AND ORGANIZATION_ID=@OrganizationId AND TEAM_ID=@Id
                 ORDER BY EMPLOYEE_ID
                """, Key(teamId), ct))).Select(value => new ProjectMember(Parse(value.EmployeeId), value.IsManager)).ToArray();
            return new(teamId, _scope.Value, Parse(row.Version), row.CreatedBy,
                new TeamInput(row.Name, row.Prefix, row.IsPublic, row.ShareProfileView, row.RequirePlanToTrack),
                Array.AsReadOnly(members));
        }

        private async Task ExecuteOne(string sql, object values, string code, CancellationToken ct)
        {
            if (await _connection.ExecuteAsync(Command(sql, values, ct)) != 1) throw new BusinessException(code);
        }
        private CommandDefinition Command(string sql, object values, CancellationToken ct)
            => new(sql, values, _transaction, _timeout, cancellationToken: ct);
        private object Key(Guid id) => Params(new { Id = Id(id) });
        private object Params(object values)
        {
            var parameters = new DynamicParameters(values);
            parameters.Add("TenantId", _scope.TenantId);
            parameters.Add("OrganizationId", _scope.OrganizationId);
            return parameters;
        }
        private void RequireScope(BusinessScope scope)
        {
            if (scope != _scope.Value) throw new BusinessException("WORK_STORAGE_CONTRACT_VIOLATION");
        }
        private static string Id(Guid id) => id.ToString("D");
        private static string? OptionalId(Guid? id) => id?.ToString("D");
        private static Guid Parse(string value)
            => Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty
                ? parsed : throw new BusinessException("WORK_STORAGE_CONTRACT_VIOLATION");
        private static Guid? OptionalParse(string? value) => value is null ? null : Parse(value);
        private static long? Ticks(DateTimeOffset? value) => value?.UtcDateTime.Ticks;
        private static DateTimeOffset? At(long? ticks) => ticks.HasValue
            ? new DateTimeOffset(ticks.Value, TimeSpan.Zero) : null;
        private static Task Unsupported() => Task.FromException(new BusinessException("WORK_STORAGE_CONTRACT_VIOLATION"));
        private static Task<T> Unsupported<T>() => Task.FromException<T>(new BusinessException("WORK_STORAGE_CONTRACT_VIOLATION"));
    }

    private sealed record ScopeKey(BusinessScope Value, string TenantId, string OrganizationId);
    private sealed class ProjectRow
    {
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string CreatedBy { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Description { get; init; }
        public string? Code { get; init; }
        public string Status { get; init; } = "";
        public long? StartTicks { get; init; }
        public long? EndTicks { get; init; }
        public bool Billable { get; init; }
        public bool IsPublic { get; init; }
        public decimal? Budget { get; init; }
        public string BudgetType { get; init; } = "";
        public string? CustomerId { get; init; }
    }
    private sealed class TeamRow
    {
        public string Id { get; init; } = "";
        public string Version { get; init; } = "";
        public string CreatedBy { get; init; } = "";
        public string Name { get; init; } = "";
        public string? Prefix { get; init; }
        public bool IsPublic { get; init; }
        public bool ShareProfileView { get; init; }
        public bool RequirePlanToTrack { get; init; }
    }
    private sealed class MemberRow
    {
        public string EmployeeId { get; init; } = "";
        public bool IsManager { get; init; }
    }
}
