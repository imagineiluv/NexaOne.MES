using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.POM.Application.WorkScopes;
using NexaOne.POM.Domain;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.POM.Infrastructure;

/// <summary>작업 대상 상태 행과 실행 원장을 원자적으로 저장하는 Dapper 어댑터입니다.</summary>
public sealed class WorkScopeRepository : QueryRepository, IWorkScopeRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly string _insertMemberSql;
    private readonly bool _isSqlServer;

    public WorkScopeRepository(EesDataSource dataSource) : base(dataSource)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _processor = new ServiceObjectProcessor(dataSource);
        _isSqlServer = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer;
        _insertMemberSql = dataSource.Provider.Kind == DatabaseProviderKind.SqlServer
            ? InsertMemberSqlSqlServer
            : InsertMemberSql;
    }

    public async Task<PomWorkScope?> GetByIdAsync(string workScopeId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ScopeRow>(
            SelectScopeSql + " WHERE WORK_SCOPE_ID = @workScopeId",
            new { workScopeId }, ct);
        return row?.ToDomain();
    }

    public async Task<PomWorkScope?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ScopeRow>(
            SelectScopeSql + " WHERE CREATE_IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToDomain();
    }

    public async Task<IReadOnlyList<PomWorkScope>> ListAsync(
        string? plantId,
        PomWorkScopeType? scopeType,
        string? targetId,
        string? parentScopeId,
        PomWorkScopeStatus? status,
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<ScopeRow>(
            SelectScopeSql + " WHERE (@plantId IS NULL OR PLANT_ID = @plantId)" +
            " AND (@scopeType IS NULL OR SCOPE_TYPE = @scopeType)" +
            " AND (@targetId IS NULL OR TARGET_ID = @targetId)" +
            " AND (@parentScopeId IS NULL OR PARENT_SCOPE_ID = @parentScopeId)" +
            " AND (@status IS NULL OR STATUS = @status)" +
            " ORDER BY CREATED_AT DESC, WORK_SCOPE_ID",
            new
            {
                plantId,
                scopeType = scopeType?.ToString(),
                targetId,
                parentScopeId,
                status = status?.ToString()
            }, ct);
        return rows.Select(static row => row.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<PomWorkScopeMember>> ListMembersAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<MemberRow>(
            MemberSelectSql + " WHERE WORK_SCOPE_ID = @workScopeId ORDER BY SEQUENCE_NO, MEMBER_ID",
            new { workScopeId }, ct);
        return rows.Select(static row => row.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<PomWorkScopeExecution>> ListExecutionsAsync(
        string workScopeId,
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<ExecutionRow>(
            SelectExecutionSql + " WHERE WORK_SCOPE_ID = @workScopeId ORDER BY OCCURRED_AT DESC, EXECUTION_ID DESC",
            new { workScopeId }, ct);
        return rows.Select(static row => row.ToDomain()).ToList();
    }

    public async Task<PomWorkScopeExecution?> GetExecutionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ExecutionRow>(
            SelectExecutionSql + " WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToDomain();
    }

    public async Task AddAsync(PomWorkScope scope, CancellationToken ct = default)
    {
        var row = ScopeRow.FromDomain(scope);
        if (scope.ParentScopeId is null)
        {
            await _processor.InsertAsync(InsertScopeSql, row, ct);
            return;
        }

        await _processor.ExecuteManyAsync(
            ct,
            (InsertScopeSql, row),
            (_insertMemberSql, MemberRow.FromDomain(scope)));
    }

    public async Task<WorkScopeWriteResult> UpdateWithExecutionAsync(
        PomWorkScope scope,
        PomWorkScopeExecution execution,
        CancellationToken ct = default)
    {
        var row = ScopeRow.FromDomain(scope);
        var executionRow = ExecutionRow.FromDomain(execution);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var result = await _processor.ExecuteInTransactionAsync(
                    async (connection, transaction) =>
                    {
                        // SQLite begins transactions deferred. Force writer ownership before the
                        // authority read so provisioning and ordinary mutation cannot both observe
                        // an unbound pristine scope. SQL Server uses the same scope->authority lock
                        // order with UPDLOCK/HOLDLOCK.
                        if (!_isSqlServer)
                        {
                            await ExecuteAsync(
                                connection,
                                transaction,
                                "UPDATE POM_WORK_SCOPE SET UPDATED_AT = UPDATED_AT WHERE WORK_SCOPE_ID = @WorkScopeId",
                                row,
                                ct).ConfigureAwait(false);
                        }

                        var locked = await QueryFirstOrDefaultInTransactionAsync<ScopeWriteLockRow>(
                            connection,
                            transaction,
                            _isSqlServer ? SelectScopeWriteLockSqlSqlServer : SelectScopeWriteLockSql,
                            row,
                            ct).ConfigureAwait(false);
                        if (locked is null)
                            return WorkScopeWriteResult.VersionConflict;

                        var projectionOwned = await QueryFirstOrDefaultInTransactionAsync<int?>(
                            connection,
                            transaction,
                            _isSqlServer ? SelectAuthorityFenceSqlSqlServer : SelectAuthorityFenceSql,
                            row,
                            ct).ConfigureAwait(false);
                        if (projectionOwned is not null)
                            return WorkScopeWriteResult.ProjectionOwned;
                        if (locked.VersionNo != row.VersionNo)
                            return WorkScopeWriteResult.VersionConflict;

                        var updated = await ExecuteAsync(
                            connection, transaction, UpdateScopeSql, row, ct).ConfigureAwait(false);
                        if (updated == 0) return WorkScopeWriteResult.VersionConflict;
                        if (updated != 1)
                            throw new DBConcurrencyException(
                                $"WorkScope update affected {updated} rows; expected exactly one.");

                        var inserted = await ExecuteAsync(
                            connection, transaction, InsertExecutionSql, executionRow, ct)
                            .ConfigureAwait(false);
                        if (inserted != 1)
                            throw new DBConcurrencyException(
                                $"WorkScope execution insert affected {inserted} rows; expected exactly one.");
                        return WorkScopeWriteResult.Applied;
                    },
                    IsolationLevel.Serializable,
                    ct).ConfigureAwait(false);
                if (result.Kind == WorkScopeWriteKind.Applied) scope.AcceptPersistedVersion();
                return result;
            }
            catch (DbException ex) when (
                !_isSqlServer
                && attempt < 6
                && IsSqliteBusy(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private const string SelectScopeWriteLockSql = """
        SELECT VERSION_NO AS VersionNo
          FROM POM_WORK_SCOPE
         WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string SelectScopeWriteLockSqlSqlServer = """
        SELECT VERSION_NO AS VersionNo
          FROM POM_WORK_SCOPE WITH (UPDLOCK, HOLDLOCK)
         WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string SelectAuthorityFenceSql = """
        SELECT 1
          FROM POM_WORK_SCOPE_PROJECTION_AUTHORITY
         WHERE WORK_SCOPE_ID = @WorkScopeId
         LIMIT 1
        """;

    private const string SelectAuthorityFenceSqlSqlServer = """
        SELECT TOP (1) 1
          FROM POM_PROJECTION_AUTHORITY_SCOPE_FENCE WITH (UPDLOCK, HOLDLOCK)
         WHERE WORK_SCOPE_ID = @WorkScopeId
        """;

    private const string SelectScopeSql = """
        SELECT WORK_SCOPE_ID AS WorkScopeId, PLANT_ID AS PlantId, SCOPE_TYPE AS ScopeType,
               TARGET_ID AS TargetId, NAME AS Name, PARENT_SCOPE_ID AS ParentScopeId,
               WORK_ORDER_ID AS WorkOrderId, CARRIER_ID AS CarrierId,
               EQUIPMENT_ID AS EquipmentId, PRODUCT_ID AS ProductId, PROCESS_ID AS ProcessId,
               RECIPE_ID AS RecipeId, RECIPE_VERSION AS RecipeVersion, PLAN_QTY AS PlanQty,
               START_QTY AS StartQty, COMPLETE_QTY AS CompleteQty, SCRAP_QTY AS ScrapQty,
               OWNER_ID AS OwnerId, STATUS AS Status, IS_HOLD AS IsHold, STARTED_AT AS StartedAt,
               COMPLETED_AT AS CompletedAt, DESCRIPTION AS Description, VERSION_NO AS VersionNo,
               CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt, UPDATED_BY AS UpdatedBy, UPDATED_AT AS UpdatedAt,
               CREATE_IDEMPOTENCY_KEY AS CreateIdempotencyKey, CREATE_REQUEST_HASH AS CreateRequestHash
        FROM POM_WORK_SCOPE
        """;

    private const string MemberSelectSql = """
        SELECT MEMBER_ID AS MemberId, WORK_SCOPE_ID AS WorkScopeId,
               MEMBER_SCOPE_ID AS MemberScopeId, MEMBER_TYPE AS MemberType,
               MEMBER_TARGET_ID AS MemberTargetId, SEQUENCE_NO AS SequenceNo,
               CREATED_AT AS CreatedAt
        FROM POM_WORK_SCOPE_MEMBER
        """;

    private const string InsertScopeSql = """
        INSERT INTO POM_WORK_SCOPE
        (WORK_SCOPE_ID, PLANT_ID, SCOPE_TYPE, TARGET_ID, NAME, PARENT_SCOPE_ID, WORK_ORDER_ID,
         CARRIER_ID, EQUIPMENT_ID,
         PRODUCT_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION, PLAN_QTY, START_QTY, COMPLETE_QTY,
         SCRAP_QTY, OWNER_ID, STATUS, IS_HOLD, STARTED_AT, COMPLETED_AT, DESCRIPTION, VERSION_NO,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT, CREATE_IDEMPOTENCY_KEY, CREATE_REQUEST_HASH)
        VALUES
        (@WorkScopeId, @PlantId, @ScopeType, @TargetId, @Name, @ParentScopeId, @WorkOrderId,
         @CarrierId, @EquipmentId,
         @ProductId, @ProcessId, @RecipeId, @RecipeVersion, @PlanQty, @StartQty, @CompleteQty,
         @ScrapQty, @OwnerId, @Status, @IsHold, @StartedAt, @CompletedAt, @Description, @VersionNo,
         @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt, @CreateIdempotencyKey, @CreateRequestHash)
        """;

    private const string InsertMemberSql = """
        INSERT INTO POM_WORK_SCOPE_MEMBER
        (MEMBER_ID, WORK_SCOPE_ID, MEMBER_SCOPE_ID, MEMBER_TYPE, MEMBER_TARGET_ID,
         SEQUENCE_NO, IDEMPOTENCY_KEY, CREATED_BY, CREATED_AT)
        SELECT @MemberId, @ParentScopeId, @MemberScopeId, @MemberType, @MemberTargetId,
               COALESCE((SELECT MAX(SEQUENCE_NO) + 1 FROM POM_WORK_SCOPE_MEMBER
                         WHERE WORK_SCOPE_ID = @ParentScopeId), 1),
               @IdempotencyKey, @CreatedBy, @CreatedAt
        """;

    // SQL Server's ReadCommitted isolation does not serialize two MAX()+1 reads for the
    // same parent. Lock the parent scope row for the duration of this transaction so the
    // sequence allocation is serialized without changing the public repository contract.
    // SQLite keeps the provider-neutral statement: its write transaction already serializes
    // concurrent writers at the database level.
    private const string InsertMemberSqlSqlServer = """
        INSERT INTO POM_WORK_SCOPE_MEMBER
        (MEMBER_ID, WORK_SCOPE_ID, MEMBER_SCOPE_ID, MEMBER_TYPE, MEMBER_TARGET_ID,
         SEQUENCE_NO, IDEMPOTENCY_KEY, CREATED_BY, CREATED_AT)
        SELECT @MemberId, @ParentScopeId, @MemberScopeId, @MemberType, @MemberTargetId,
               COALESCE((SELECT MAX(SEQUENCE_NO) + 1 FROM POM_WORK_SCOPE_MEMBER
                         WHERE WORK_SCOPE_ID = @ParentScopeId), 1),
               @IdempotencyKey, @CreatedBy, @CreatedAt
          FROM POM_WORK_SCOPE WITH (UPDLOCK, HOLDLOCK)
         WHERE WORK_SCOPE_ID = @ParentScopeId
        """;

    private const string UpdateScopeSql = """
        UPDATE POM_WORK_SCOPE SET
          PLANT_ID=@PlantId, SCOPE_TYPE=@ScopeType, TARGET_ID=@TargetId, NAME=@Name,
          PARENT_SCOPE_ID=@ParentScopeId, WORK_ORDER_ID=@WorkOrderId, CARRIER_ID=@CarrierId,
          EQUIPMENT_ID=@EquipmentId, PRODUCT_ID=@ProductId,
          PROCESS_ID=@ProcessId, RECIPE_ID=@RecipeId, RECIPE_VERSION=@RecipeVersion,
          PLAN_QTY=@PlanQty, START_QTY=@StartQty, COMPLETE_QTY=@CompleteQty, SCRAP_QTY=@ScrapQty,
          OWNER_ID=@OwnerId, STATUS=@Status, IS_HOLD=@IsHold, STARTED_AT=@StartedAt,
          COMPLETED_AT=@CompletedAt, DESCRIPTION=@Description, UPDATED_BY=@UpdatedBy,
          UPDATED_AT=@UpdatedAt, VERSION_NO=VERSION_NO+1
        WHERE WORK_SCOPE_ID=@WorkScopeId AND VERSION_NO=@VersionNo
        """;

    private const string SelectExecutionSql = """
        SELECT EXECUTION_ID AS ExecutionId, WORK_SCOPE_ID AS WorkScopeId,
               IDEMPOTENCY_KEY AS IdempotencyKey, ACTION AS Action,
               FROM_STATUS AS FromStatus, TO_STATUS AS ToStatus, GOOD_QTY AS GoodQty,
               DEFECT_QTY AS DefectQty, USER_ID AS UserId, EQUIPMENT_ID AS EquipmentId,
               CLIENT_CHANNEL AS ClientChannel, DEVICE_ID AS DeviceId, OCCURRED_AT AS OccurredAt,
               REMARK AS Remark, EXPECTED_VERSION AS ExpectedVersion, RESULT_VERSION AS ResultVersion,
               CARRIER_ID AS CarrierId, RESULT_CODE AS ResultCode,
               RESULT_METADATA_JSON AS ResultMetadataJson
        FROM POM_WORK_SCOPE_EXECUTION
        """;

    private const string InsertExecutionSql = """
        INSERT INTO POM_WORK_SCOPE_EXECUTION
        (EXECUTION_ID, WORK_SCOPE_ID, IDEMPOTENCY_KEY, ACTION, FROM_STATUS, TO_STATUS,
         GOOD_QTY, DEFECT_QTY, USER_ID, EQUIPMENT_ID, CLIENT_CHANNEL, DEVICE_ID, OCCURRED_AT,
         REMARK, EXPECTED_VERSION, RESULT_VERSION, CARRIER_ID, RESULT_CODE,
         RESULT_METADATA_JSON, CREATED_BY, CREATED_AT)
        VALUES
        (@ExecutionId, @WorkScopeId, @IdempotencyKey, @Action, @FromStatus, @ToStatus,
         @GoodQty, @DefectQty, @UserId, @EquipmentId, @ClientChannel, @DeviceId, @OccurredAt,
         @Remark, @ExpectedVersion, @ResultVersion, @CarrierId, @ResultCode,
         @ResultMetadataJson, @UserId, @OccurredAt)
        """;

    private static Task<T?> QueryFirstOrDefaultInTransactionAsync<T>(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken ct) => connection.QueryFirstOrDefaultAsync<T>(new CommandDefinition(
        sql,
        parameters,
        transaction,
        cancellationToken: ct));

    private static Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameters,
        CancellationToken ct) => connection.ExecuteAsync(new CommandDefinition(
        sql,
        parameters,
        transaction,
        cancellationToken: ct));

    private static bool IsSqliteBusy(DbException exception) =>
        exception.Message.Contains("locked", StringComparison.OrdinalIgnoreCase)
        || exception.Message.Contains("busy", StringComparison.OrdinalIgnoreCase);

    private sealed class ScopeWriteLockRow
    {
        public int VersionNo { get; set; }
    }

    private sealed class ScopeRow
    {
        public string WorkScopeId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string ScopeType { get; set; } = string.Empty;
        public string TargetId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? ParentScopeId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? CarrierId { get; set; }
        public string? EquipmentId { get; set; }
        public string? ProductId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public decimal? PlanQty { get; set; }
        public decimal StartQty { get; set; }
        public decimal CompleteQty { get; set; }
        public decimal ScrapQty { get; set; }
        public string? OwnerId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string IsHold { get; set; } = "N";
        public DateTime? StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string? Description { get; set; }
        public int VersionNo { get; set; }
        public string CreatedBy { get; set; } = "SYSTEM";
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }
        public string CreateIdempotencyKey { get; set; } = string.Empty;
        public string CreateRequestHash { get; set; } = string.Empty;

        public PomWorkScope ToDomain() => PomWorkScope.Restore(
            WorkScopeId, PlantId, Enum.Parse<PomWorkScopeType>(ScopeType, true), TargetId, Name,
            ParentScopeId, WorkOrderId, CarrierId, EquipmentId, ProductId, ProcessId, RecipeId,
            RecipeVersion, PlanQty,
            StartQty, CompleteQty, ScrapQty, OwnerId,
            Enum.Parse<PomWorkScopeStatus>(Status, true),
            string.Equals(IsHold, "Y", StringComparison.OrdinalIgnoreCase), StartedAt, CompletedAt,
            Description, VersionNo, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt,
            CreateIdempotencyKey, CreateRequestHash);

        public static ScopeRow FromDomain(PomWorkScope scope, string? actor = null, DateTime? now = null)
        {
            var stamp = now ?? scope.UpdatedAt ?? DateTime.UtcNow;
            var user = actor ?? scope.UpdatedBy ?? CurrentUserContext.UserId ?? "SYSTEM";
            return new ScopeRow
            {
                WorkScopeId = scope.Id,
                PlantId = scope.PlantId,
                ScopeType = scope.ScopeType.ToString(),
                TargetId = scope.TargetId,
                Name = scope.Name,
                ParentScopeId = scope.ParentScopeId,
                WorkOrderId = scope.WorkOrderId,
                CarrierId = scope.CarrierId,
                EquipmentId = scope.EquipmentId,
                ProductId = scope.ProductId,
                ProcessId = scope.ProcessId,
                RecipeId = scope.RecipeId,
                RecipeVersion = scope.RecipeVersion,
                PlanQty = scope.PlanQty,
                StartQty = scope.StartQty,
                CompleteQty = scope.CompleteQty,
                ScrapQty = scope.ScrapQty,
                OwnerId = scope.OwnerId,
                Status = scope.Status.ToString(),
                IsHold = scope.IsHold ? "Y" : "N",
                StartedAt = scope.StartedAt,
                CompletedAt = scope.CompletedAt,
                Description = scope.Description,
                VersionNo = scope.VersionNo,
                CreatedBy = string.IsNullOrWhiteSpace(scope.CreatedBy) ? user : scope.CreatedBy,
                CreatedAt = scope.CreatedAt,
                UpdatedBy = user,
                UpdatedAt = stamp,
                CreateIdempotencyKey = scope.CreateIdempotencyKey ?? string.Empty,
                CreateRequestHash = scope.CreateRequestHash ?? string.Empty
            };
        }
    }

    private sealed class MemberRow
    {
        public string MemberId { get; set; } = string.Empty;
        public string WorkScopeId { get; set; } = string.Empty;
        public string MemberScopeId { get; set; } = string.Empty;
        public string MemberType { get; set; } = string.Empty;
        public string MemberTargetId { get; set; } = string.Empty;
        public int SequenceNo { get; set; }
        public DateTime CreatedAt { get; set; }
        public string ParentScopeId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string CreatedBy { get; set; } = "SYSTEM";

        public PomWorkScopeMember ToDomain() => new(
            MemberId, WorkScopeId, MemberScopeId,
            Enum.Parse<PomWorkScopeType>(MemberType, true), MemberTargetId,
            SequenceNo, CreatedAt);

        public static MemberRow FromDomain(PomWorkScope scope) => new()
        {
            MemberId = Guid.NewGuid().ToString("N"),
            WorkScopeId = scope.ParentScopeId ?? string.Empty,
            MemberScopeId = scope.Id,
            MemberType = scope.ScopeType.ToString(),
            MemberTargetId = scope.TargetId,
            CreatedAt = DateTime.UtcNow,
            ParentScopeId = scope.ParentScopeId ?? string.Empty,
            IdempotencyKey = $"work-scope:member:{scope.Id}",
            CreatedBy = scope.CreatedBy
        };
    }

    private sealed class ExecutionRow
    {
        public string ExecutionId { get; set; } = string.Empty;
        public string WorkScopeId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string FromStatus { get; set; } = string.Empty;
        public string ToStatus { get; set; } = string.Empty;
        public decimal? GoodQty { get; set; }
        public decimal? DefectQty { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string? EquipmentId { get; set; }
        public string ClientChannel { get; set; } = "MES";
        public string? DeviceId { get; set; }
        public DateTime OccurredAt { get; set; }
        public string? Remark { get; set; }
        public int? ExpectedVersion { get; set; }
        public int? ResultVersion { get; set; }
        public string? CarrierId { get; set; }
        public string? ResultCode { get; set; }
        public string? ResultMetadataJson { get; set; }

        public PomWorkScopeExecution ToDomain() => new(
            ExecutionId, WorkScopeId, IdempotencyKey,
            Enum.Parse<PomWorkScopeAction>(Action, true),
            Enum.Parse<PomWorkScopeStatus>(FromStatus, true),
            Enum.Parse<PomWorkScopeStatus>(ToStatus, true), GoodQty, DefectQty, UserId,
            EquipmentId, ClientChannel, DeviceId, OccurredAt, Remark, ExpectedVersion, ResultVersion,
            CarrierId, ResultCode, ResultMetadataJson);

        public static ExecutionRow FromDomain(PomWorkScopeExecution execution) => new()
        {
            ExecutionId = execution.ExecutionId,
            WorkScopeId = execution.WorkScopeId,
            IdempotencyKey = execution.IdempotencyKey,
            Action = execution.Action.ToString(),
            FromStatus = execution.FromStatus.ToString(),
            ToStatus = execution.ToStatus.ToString(),
            GoodQty = execution.GoodQty,
            DefectQty = execution.DefectQty,
            UserId = execution.UserId,
            EquipmentId = execution.EquipmentId,
            ClientChannel = execution.ClientChannel,
            DeviceId = execution.DeviceId,
            OccurredAt = execution.OccurredAt,
            Remark = execution.Remark,
            ExpectedVersion = execution.ExpectedVersion,
            ResultVersion = execution.ResultVersion,
            CarrierId = execution.CarrierId,
            ResultCode = execution.ResultCode,
            ResultMetadataJson = execution.ResultMetadataJson
        };
    }
}
