using System.Data;
using System.Data.Common;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;

namespace NexaOne.IVT.Infrastructure;

internal sealed class FeedSessionRepository : QueryRepository, IFeedSessionRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly int? _commandTimeoutSeconds;

    public FeedSessionRepository(EesDataSource dataSource) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _commandTimeoutSeconds = dataSource.QueryGatewayOptions.CommandTimeoutSeconds;
        if (_commandTimeoutSeconds is <= 0)
            throw new ArgumentOutOfRangeException(nameof(dataSource), "Command timeout must be positive.");
    }

    public async Task<FeedSessionState?> GetAsync(
        string feedSessionId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT FEED_SESSION_ID AS FeedSessionId, PLANT_ID AS PlantId,
                   EQUIPMENT_ID AS EquipmentId, FEED_POINT_ID AS FeedPointId,
                   MATERIAL_LOT_ID AS MaterialLotId, MATERIAL_ID AS MaterialId,
                   PROCESS_LOT_ID AS ProcessLotId, WORK_ORDER_ID AS WorkOrderId,
                   PROCESS_ID AS ProcessId, RECIPE_ID AS RecipeId,
                   RECIPE_VERSION AS RecipeVersion, MOUNTED_AT AS MountedAt,
                   MOUNTED_BY AS MountedBy, UNMOUNTED_AT AS UnmountedAt,
                   UNMOUNTED_BY AS UnmountedBy, STATUS AS Status,
                   VERSION_NO AS VersionNo, CREATED_BY AS CreatedBy,
                   CREATED_AT AS CreatedAt, UPDATED_BY AS UpdatedBy,
                   UPDATED_AT AS UpdatedAt
              FROM IVT_MATERIAL_FEED_SESSION
             WHERE FEED_SESSION_ID = @feedSessionId
            """;
        var row = await QueryFirstOrDefaultAsync<SessionRow>(sql, new { feedSessionId }, ct);
        return row?.ToDomain();
    }

    public Task<FeedSessionWrite?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default) =>
        GetCommandAsync("IDEMPOTENCY_KEY = @idempotencyKey", new { idempotencyKey }, ct);

    public Task<FeedSessionWrite?> GetBySourceEventAsync(
        string sourceSystem,
        string sourceEventId,
        CancellationToken ct = default) =>
        GetCommandAsync(
            "SOURCE_SYSTEM = @sourceSystem AND SOURCE_EVENT_ID = @sourceEventId",
            new { sourceSystem, sourceEventId },
            ct);

    public async Task<bool> TryMountAsync(
        FeedSessionState session,
        FeedSessionWrite write,
        CancellationToken ct = default)
    {
        var parameters = Parameters(write);
        const string lockAvailableLot = """
            UPDATE IVT_MATERIAL_LOT SET
                   ACTIVE_FEED_SESSION_ID = ACTIVE_FEED_SESSION_ID
             WHERE LOT_ID = @MaterialLotId
               AND MATERIAL_ID = @MaterialId
               AND STATUS = 'InStock'
               AND CURRENT_QTY > 0
               AND ACTIVE_FEED_SESSION_ID IS NULL
            """;
        const string verifyReservation = """
            UPDATE IVT_MATERIAL_LOT SET
                   ACTIVE_FEED_SESSION_ID = ACTIVE_FEED_SESSION_ID
             WHERE LOT_ID = @MaterialLotId
               AND ACTIVE_FEED_SESSION_ID = @FeedSessionId
            """;
        const string insertSession = """
            INSERT INTO IVT_MATERIAL_FEED_SESSION
              (FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
               MATERIAL_LOT_ID, MATERIAL_ID, PROCESS_LOT_ID, WORK_ORDER_ID,
               PROCESS_ID, RECIPE_ID, RECIPE_VERSION, MOUNTED_AT, MOUNTED_BY,
               UNMOUNTED_AT, UNMOUNTED_BY, STATUS, VERSION_NO,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @FeedSessionId, @PlantId, @EquipmentId, @FeedPointId,
                   @MaterialLotId, @MaterialId, @ProcessLotId, @WorkOrderId,
                   @ProcessId, @RecipeId, @RecipeVersion, @MountedAt, @MountedBy,
                   NULL, NULL, 'Mounted', 1,
                   @ActorId, @CommittedAt, @ActorId, @CommittedAt
             WHERE @ExpectedVersion = 0
               AND EXISTS (
                       SELECT 1 FROM IVT_MATERIAL_LOT
                        WHERE LOT_ID = @MaterialLotId AND MATERIAL_ID = @MaterialId
                          AND STATUS = 'InStock' AND CURRENT_QTY > 0)
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_MATERIAL_FEED_SESSION
                        WHERE FEED_SESSION_ID = @FeedSessionId)
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_MATERIAL_FEED_SESSION
                        WHERE PLANT_ID = @PlantId AND EQUIPMENT_ID = @EquipmentId
                          AND FEED_POINT_ID = @FeedPointId
                          AND STATUS <> 'Cancelled'
                          AND (UNMOUNTED_AT IS NULL OR UNMOUNTED_AT > @MountedAt))
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_FEED_SESSION_COMMAND
                        WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_FEED_SESSION_COMMAND
                        WHERE SOURCE_SYSTEM = @SourceSystem AND SOURCE_EVENT_ID = @SourceEventId)
            """;
        try
        {
            return await _processor.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    // Lock the material LOT first so lifecycle writes and concurrent mounts keep one
                    // global order. V151's DB trigger reserves that row atomically after the session
                    // insert; inserting first is required by the composite reservation FK.
                    if (await ExecuteAsync(connection, transaction, lockAvailableLot, parameters, ct) != 1)
                        return false;
                    if (await ExecuteAsync(connection, transaction, insertSession, parameters, ct) != 1)
                        throw new FeedSessionWriteConflictException();
                    if (await ExecuteAsync(connection, transaction, verifyReservation, parameters, ct) != 1)
                        throw new DBConcurrencyException(
                            "Feed-session mount did not retain exactly one matching material-LOT reservation.");
                    if (await ExecuteAsync(connection, transaction, InsertCommandSql, parameters, ct) != 1)
                        throw new DBConcurrencyException("Feed-session mount command was not inserted exactly once.");
                    return true;
                },
                IsolationLevel.Serializable,
                ct);
        }
        catch (FeedSessionWriteConflictException)
        {
            return false;
        }
        catch (DbException)
        {
            if (await HasKnownConflictAsync(write, includeMountIdentity: true, ct)) return false;
            throw;
        }
    }

    public async Task<bool> TryCloseAsync(
        FeedSessionState session,
        int expectedVersion,
        FeedSessionWrite write,
        CancellationToken ct = default)
    {
        var parameters = Parameters(write);
        const string lockReservedLot = """
            UPDATE IVT_MATERIAL_LOT SET
                   ACTIVE_FEED_SESSION_ID = ACTIVE_FEED_SESSION_ID
             WHERE LOT_ID = @MaterialLotId
               AND ACTIVE_FEED_SESSION_ID = @FeedSessionId
            """;
        const string updateSession = """
            UPDATE IVT_MATERIAL_FEED_SESSION SET
                   UNMOUNTED_AT = @UnmountedAt,
                   UNMOUNTED_BY = @UnmountedBy,
                   STATUS = @ResultStatus,
                   VERSION_NO = @ResultVersion,
                   UPDATED_BY = @ActorId,
                   UPDATED_AT = @CommittedAt
             WHERE FEED_SESSION_ID = @FeedSessionId
               AND STATUS = 'Mounted'
               AND UNMOUNTED_AT IS NULL
               AND VERSION_NO = @ExpectedVersion
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_FEED_SESSION_COMMAND
                        WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_FEED_SESSION_COMMAND
                        WHERE SOURCE_SYSTEM = @SourceSystem AND SOURCE_EVENT_ID = @SourceEventId)
            """;
        try
        {
            return await _processor.ExecuteInTransactionAsync(
                async (connection, transaction) =>
                {
                    // Keep the global lock order material LOT -> feed session. Unmount only closes
                    // the physical interval. The LOT reservation deliberately remains until a
                    // future durable FDC-watermark + terminal-inbox finalize protocol can prove
                    // that no pre-unmount TRACE can arrive late.
                    if (await ExecuteAsync(connection, transaction, lockReservedLot, parameters, ct) != 1)
                        return false;
                    if (await ExecuteAsync(connection, transaction, updateSession, parameters, ct) != 1)
                        throw new FeedSessionWriteConflictException();
                    if (await ExecuteAsync(connection, transaction, InsertCommandSql, parameters, ct) != 1)
                        throw new DBConcurrencyException("Feed-session close command was not inserted exactly once.");
                    return true;
                },
                IsolationLevel.Serializable,
                ct);
        }
        catch (FeedSessionWriteConflictException)
        {
            return false;
        }
        catch (DbException)
        {
            if (await HasKnownConflictAsync(write, includeMountIdentity: false, ct))
                return false;
            throw;
        }
    }

    private async Task<bool> HasKnownConflictAsync(
        FeedSessionWrite write,
        bool includeMountIdentity,
        CancellationToken ct)
    {
        if (await GetByIdempotencyKeyAsync(write.IdempotencyKey, ct) is not null
            || await GetBySourceEventAsync(write.SourceSystem, write.SourceEventId, ct) is not null)
        {
            return true;
        }
        if (!includeMountIdentity) return false;
        if (await GetAsync(write.Result.FeedSessionId, ct) is not null) return true;

        const string sql = """
            SELECT COUNT(*)
              FROM IVT_MATERIAL_FEED_SESSION
             WHERE PLANT_ID = @PlantId AND EQUIPMENT_ID = @EquipmentId
               AND FEED_POINT_ID = @FeedPointId
               AND STATUS <> 'Cancelled'
               AND (UNMOUNTED_AT IS NULL OR UNMOUNTED_AT > @MountedAt)
            """;
        return await CountAsync(sql, write.Result, ct) > 0;
    }

    private async Task<FeedSessionWrite?> GetCommandAsync(
        string predicate,
        object parameter,
        CancellationToken ct)
    {
        var sql = CommandSelectSql + $" WHERE {predicate}";
        var row = await QueryFirstOrDefaultAsync<CommandRow>(sql, parameter, ct);
        return row?.ToDomain();
    }

    private const string CommandSelectSql = """
        SELECT COMMAND_ID AS CommandId, COMMAND_TYPE AS CommandType,
               IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
               FEED_SESSION_ID AS FeedSessionId, PLANT_ID AS PlantId,
               EQUIPMENT_ID AS EquipmentId, FEED_POINT_ID AS FeedPointId,
               MATERIAL_LOT_ID AS MaterialLotId, MATERIAL_ID AS MaterialId,
               PROCESS_LOT_ID AS ProcessLotId, WORK_ORDER_ID AS WorkOrderId,
               PROCESS_ID AS ProcessId, RECIPE_ID AS RecipeId,
               RECIPE_VERSION AS RecipeVersion, MOUNTED_AT AS MountedAt,
               MOUNTED_BY AS MountedBy, UNMOUNTED_AT AS UnmountedAt,
               UNMOUNTED_BY AS UnmountedBy, RESULT_STATUS AS ResultStatus,
               EXPECTED_VERSION AS ExpectedVersion, RESULT_VERSION AS ResultVersion,
               ACTOR_ID AS ActorId, OCCURRED_AT AS OccurredAt,
               SOURCE_SYSTEM AS SourceSystem, SOURCE_EVENT_ID AS SourceEventId,
               CORRELATION_ID AS CorrelationId, REASON AS Reason,
               CREATED_AT AS CreatedAt
          FROM IVT_FEED_SESSION_COMMAND
        """;

    private const string InsertCommandSql = """
        INSERT INTO IVT_FEED_SESSION_COMMAND
          (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH,
           FEED_SESSION_ID, PLANT_ID, EQUIPMENT_ID, FEED_POINT_ID,
           MATERIAL_LOT_ID, MATERIAL_ID, PROCESS_LOT_ID, WORK_ORDER_ID,
           PROCESS_ID, RECIPE_ID, RECIPE_VERSION, MOUNTED_AT, MOUNTED_BY,
           UNMOUNTED_AT, UNMOUNTED_BY, RESULT_STATUS,
           EXPECTED_VERSION, RESULT_VERSION, ACTOR_ID, OCCURRED_AT,
           SOURCE_SYSTEM, SOURCE_EVENT_ID, CORRELATION_ID, REASON,
           CREATED_BY, CREATED_AT)
        VALUES
          (@CommandId, @Operation, @IdempotencyKey, @RequestHash,
           @FeedSessionId, @PlantId, @EquipmentId, @FeedPointId,
           @MaterialLotId, @MaterialId, @ProcessLotId, @WorkOrderId,
           @ProcessId, @RecipeId, @RecipeVersion, @MountedAt, @MountedBy,
           @UnmountedAt, @UnmountedBy, @ResultStatus,
           @ExpectedVersion, @ResultVersion, @ActorId, @OccurredAt,
           @SourceSystem, @SourceEventId, @CorrelationId, @Reason,
           @ActorId, @CommittedAt)
        """;

    private static DynamicParameters Parameters(FeedSessionWrite write)
    {
        var state = write.Result;
        var parameters = new DynamicParameters();
        parameters.Add("CommandId", write.CommandId);
        parameters.Add("Operation", write.Operation);
        parameters.Add("IdempotencyKey", write.IdempotencyKey);
        parameters.Add("RequestHash", write.RequestHash);
        parameters.Add("FeedSessionId", state.FeedSessionId);
        parameters.Add("PlantId", state.PlantId);
        parameters.Add("EquipmentId", state.EquipmentId);
        parameters.Add("FeedPointId", state.FeedPointId);
        parameters.Add("MaterialLotId", state.MaterialLotId);
        parameters.Add("MaterialId", state.MaterialId);
        parameters.Add("ProcessLotId", state.ProcessLotId);
        parameters.Add("WorkOrderId", state.WorkOrderId);
        parameters.Add("ProcessId", state.ProcessId);
        parameters.Add("RecipeId", state.RecipeId);
        parameters.Add("RecipeVersion", state.RecipeVersion);
        parameters.Add("MountedAt", state.MountedAt);
        parameters.Add("MountedBy", state.MountedBy);
        parameters.Add("UnmountedAt", state.UnmountedAt);
        parameters.Add("UnmountedBy", state.UnmountedBy);
        parameters.Add("ResultStatus", state.Status);
        parameters.Add("ExpectedVersion", write.ExpectedVersion);
        parameters.Add("ResultVersion", state.Version);
        parameters.Add("ActorId", write.ActorId);
        parameters.Add("OccurredAt", write.OccurredAt);
        parameters.Add("SourceSystem", write.SourceSystem);
        parameters.Add("SourceEventId", write.SourceEventId);
        parameters.Add("CorrelationId", write.CorrelationId);
        parameters.Add("Reason", write.Reason);
        parameters.Add("CommittedAt", state.UpdatedAt);
        return parameters;
    }

    private sealed class SessionRow
    {
        public string FeedSessionId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string FeedPointId { get; set; } = string.Empty;
        public string MaterialLotId { get; set; } = string.Empty;
        public string MaterialId { get; set; } = string.Empty;
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public DateTime MountedAt { get; set; }
        public string MountedBy { get; set; } = string.Empty;
        public DateTime? UnmountedAt { get; set; }
        public string? UnmountedBy { get; set; }
        public string Status { get; set; } = string.Empty;
        public int VersionNo { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }

        public FeedSessionState ToDomain() => new(
            FeedSessionId, PlantId, EquipmentId, FeedPointId, MaterialLotId, MaterialId,
            ProcessLotId, WorkOrderId, ProcessId, RecipeId, RecipeVersion,
            Utc(MountedAt), MountedBy, Utc(UnmountedAt), UnmountedBy, Status, VersionNo,
            CreatedBy, Utc(CreatedAt), UpdatedBy, Utc(UpdatedAt));
    }

    private sealed class CommandRow
    {
        public string CommandId { get; set; } = string.Empty;
        public string CommandType { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string FeedSessionId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string FeedPointId { get; set; } = string.Empty;
        public string MaterialLotId { get; set; } = string.Empty;
        public string MaterialId { get; set; } = string.Empty;
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public DateTime MountedAt { get; set; }
        public string MountedBy { get; set; } = string.Empty;
        public DateTime? UnmountedAt { get; set; }
        public string? UnmountedBy { get; set; }
        public string ResultStatus { get; set; } = string.Empty;
        public int ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public string ActorId { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public string SourceSystem { get; set; } = string.Empty;
        public string SourceEventId { get; set; } = string.Empty;
        public string? CorrelationId { get; set; }
        public string? Reason { get; set; }
        public DateTime CreatedAt { get; set; }

        public FeedSessionWrite ToDomain()
        {
            var state = new FeedSessionState(
                FeedSessionId, PlantId, EquipmentId, FeedPointId, MaterialLotId, MaterialId,
                ProcessLotId, WorkOrderId, ProcessId, RecipeId, RecipeVersion,
                Utc(MountedAt), MountedBy, Utc(UnmountedAt), UnmountedBy, ResultStatus, ResultVersion,
                MountedBy, Utc(MountedAt), ActorId, Utc(CreatedAt));
            return new FeedSessionWrite(
                CommandId, CommandType, IdempotencyKey, RequestHash, state,
                ExpectedVersion, ActorId, Utc(OccurredAt), SourceSystem, SourceEventId,
                CorrelationId, Reason);
        }
    }

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } timestamp
        ? Utc(timestamp)
        : null;

    private Task<int> ExecuteAsync(
        DbConnection connection,
        DbTransaction transaction,
        string sql,
        object parameter,
        CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(
            sql, parameter, transaction,
            commandTimeout: _commandTimeoutSeconds, cancellationToken: ct));

    private sealed class FeedSessionWriteConflictException : Exception;

}
