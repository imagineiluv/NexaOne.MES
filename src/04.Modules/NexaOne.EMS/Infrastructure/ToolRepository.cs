using NexaOne.EMS.Application.Tools;
using NexaOne.Infrastructure.Persistence;
using System.Data.Common;
using System.Text.Json;

namespace NexaOne.EMS.Infrastructure;

public sealed class ToolRepository : QueryRepository, IToolRepository
{
    private readonly ServiceObjectProcessor _processor;
    public ToolRepository(EesDataSource dataSource) : base(dataSource) => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<ToolRecord?> GetToolAsync(string toolId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ToolRow>(ToolSelect + " WHERE TOOL_ID=@toolId", new { toolId }, ct);
        return row?.ToRecord();
    }

    public async Task<ToolSaveCommandRecord?> GetSaveCommandAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ToolSaveCommandRow>(
            ToolSaveCommandSelect + " WHERE IDEMPOTENCY_KEY=@idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TrySaveToolAsync(
        ToolRecord t,
        string? expectedStatus,
        int expectedVersion,
        ToolSaveCommandRecord command,
        string actorId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var p = new
        {
            t.ToolId, t.ToolName, t.ToolType, t.ToolNumber, t.SerialNumber, t.EquipmentClassId,
            t.MaxUseCount, t.MaxUseMinutes, t.CurrentUseCount, t.CurrentUseMinutes,
            t.InspectionCycleDays, t.CalibrationCycleDays, t.LastInspectedAt, t.LastCalibratedAt,
            t.NextInspectionDueAt, t.NextCalibrationDueAt, t.Status, t.Location, t.IsActive,
            t.Version, ExpectedStatus = expectedStatus, ExpectedVersion = expectedVersion,
            command.CommandId, command.IdempotencyKey, command.RequestHash,
            command.ResultVersion, command.ResultJson,
            ActorId = actorId, Now = now,
        };
        try
        {
            var guard = expectedVersion == 0 ? InsertToolSql : UpdateToolSql;
            return await _processor.ExecuteGuardedManyAsync(
                ct, (guard, p), (InsertToolSaveCommandSql, p));
        }
        catch (DbException exception)
        {
            if (IsUniqueViolation(exception)) return false;
            if (await GetSaveCommandAsync(command.IdempotencyKey, ct) is not null) return false;
            throw;
        }
    }

    public Task<bool> TrySaveToolAsync(
        ToolRecord tool,
        string? expectedStatus,
        string actorId,
        CancellationToken ct = default)
    {
        var expectedVersion = tool.Version - 1;
        var command = new ToolSaveCommandRecord(
            $"TSC_{Guid.NewGuid():N}", $"tool-repository:{Guid.NewGuid():N}",
            $"repository:{tool.ToolId}:{tool.Version}", tool.ToolId, expectedVersion,
            tool.Version, JsonSerializer.Serialize(tool), actorId, DateTime.UtcNow);
        return TrySaveToolAsync(
            tool, expectedStatus, expectedVersion, command, actorId, ct);
    }

    public async Task<ToolMountRecord?> GetMountAsync(string mountId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MountRow>(MountSelect + " WHERE MOUNT_ID=@mountId", new { mountId }, ct);
        return row?.ToRecord();
    }

    public async Task<ToolMountRecord?> GetActiveMountAsync(
        string toolId,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MountRow>(
            MountSelect + " WHERE TOOL_ID=@toolId AND UNMOUNTED_AT IS NULL",
            new { toolId }, ct);
        return row?.ToRecord();
    }

    public async Task<ToolMountRecord?> GetActiveMountAtPositionAsync(
        string equipmentId,
        string positionCode,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MountRow>(
            MountSelect + @" WHERE EQUIPMENT_ID=@equipmentId AND POSITION_CODE=@positionCode
                             AND UNMOUNTED_AT IS NULL",
            new { equipmentId, positionCode }, ct);
        return row?.ToRecord();
    }

    public async Task<ToolMountRecord?> GetMountByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MountRow>(MountSelect + " WHERE IDEMPOTENCY_KEY=@key", new { key }, ct);
        return row?.ToRecord();
    }

    public async Task<ToolMountRecord?> GetUnmountByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MountRow>(MountSelect + " WHERE UNMOUNT_IDEMPOTENCY_KEY=@key", new { key }, ct);
        return row?.ToRecord();
    }

    public async Task<DateTime?> GetLatestUsageAtAsync(string mountId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<LatestUsageRow>(
            "SELECT MAX(USED_AT) AS UsedAt FROM EMS_TOOL_USAGE_HISTORY WHERE MOUNT_ID=@mountId",
            new { mountId }, ct);
        return row?.UsedAt;
    }

    public async Task<bool> TryMountAsync(
        ToolMountRecord m,
        string? expectedEquipmentClassId,
        CancellationToken ct = default)
    {
        var p = new
        {
            m.MountId, m.IdempotencyKey, m.RequestHash, m.ToolId, m.EquipmentId, m.PositionCode,
            m.MountedAt, m.MountedBy, m.CreatedAt,
            ExpectedEquipmentClassId = expectedEquipmentClassId,
        };
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (@"UPDATE EMS_TOOL SET STATUS='Mounted', UPDATED_BY=@MountedBy, UPDATED_AT=@CreatedAt
               WHERE TOOL_ID=@ToolId AND IS_ACTIVE=1 AND STATUS='Available'
                  AND ((EQUIPMENT_CLASS_ID=@ExpectedEquipmentClassId)
                       OR (EQUIPMENT_CLASS_ID IS NULL AND @ExpectedEquipmentClassId IS NULL))
                  AND (MAX_USE_COUNT IS NULL OR CURRENT_USE_COUNT < MAX_USE_COUNT)
                  AND (MAX_USE_MINUTES IS NULL OR CURRENT_USE_MINUTES < MAX_USE_MINUTES)
                  AND (NEXT_INSPECTION_DUE_AT IS NULL OR NEXT_INSPECTION_DUE_AT > @MountedAt)
                  AND (NEXT_CALIBRATION_DUE_AT IS NULL OR NEXT_CALIBRATION_DUE_AT > @MountedAt)
                  AND NOT EXISTS (SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY
                                  WHERE TOOL_ID=@ToolId AND UNMOUNTED_AT IS NULL)
                  AND (@PositionCode IS NULL OR NOT EXISTS (
                      SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY
                       WHERE EQUIPMENT_ID=@EquipmentId AND POSITION_CODE=@PositionCode
                         AND UNMOUNTED_AT IS NULL))", p),
                (@"INSERT INTO EMS_TOOL_MOUNT_HISTORY
               (MOUNT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, EQUIPMENT_ID, POSITION_CODE,
                MOUNTED_AT, MOUNTED_BY, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
               VALUES
               (@MountId, @IdempotencyKey, @RequestHash, @ToolId, @EquipmentId, @PositionCode,
                 @MountedAt, @MountedBy, @MountedBy, @CreatedAt, @MountedBy, @CreatedAt)", p));
        }
        catch (DbException exception)
        {
            if (IsUniqueViolation(exception)) return false;
            if (await GetMountByIdempotencyKeyAsync(m.IdempotencyKey, ct) is not null) return false;
            throw;
        }
    }

    public async Task<bool> TryUnmountAsync(ToolMountRecord mount, string key, string hash, DateTime at,
        string actorId, string? reason, CancellationToken ct = default)
    {
        var p = new { mount.MountId, mount.ToolId, Key = key, Hash = hash, At = at, ActorId = actorId, Reason = reason };
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (@"UPDATE EMS_TOOL_MOUNT_HISTORY SET
                 UNMOUNTED_AT=@At, UNMOUNTED_BY=@ActorId, UNMOUNT_IDEMPOTENCY_KEY=@Key,
                 UNMOUNT_REQUEST_HASH=@Hash, UNMOUNT_REASON=@Reason,
                 UPDATED_BY=@ActorId, UPDATED_AT=@At
               WHERE MOUNT_ID=@MountId AND UNMOUNTED_AT IS NULL
                 AND NOT EXISTS (
                     SELECT 1 FROM EMS_TOOL_USAGE_HISTORY U
                     WHERE U.MOUNT_ID=@MountId AND U.USED_AT > @At
                 )", p),
            (@"UPDATE EMS_TOOL SET
                 STATUS=CASE WHEN STATUS='Mounted' THEN 'Available' ELSE STATUS END,
                 UPDATED_BY=@ActorId, UPDATED_AT=@At
               WHERE TOOL_ID=@ToolId", p));
        }
        catch (DbException)
        {
            if (await GetUnmountByIdempotencyKeyAsync(key, ct) is not null) return false;
            throw;
        }
    }

    public async Task<ToolUsageRecord?> GetUsageByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<UsageRow>(UsageSelect + " WHERE IDEMPOTENCY_KEY=@key", new { key }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryRecordUsageAsync(
        ToolUsageRecord u,
        string? expectedEquipmentClassId,
        CancellationToken ct = default)
    {
        var p = new
        {
            u.UsageId, u.IdempotencyKey, u.RequestHash, u.ToolId, u.MountId, u.EquipmentId,
            u.ProcessLotId, u.WorkOrderId, u.ProcessId, u.RecipeId, u.RecipeVersion,
            u.UseCount, u.UseMinutes, u.UsedAt, u.UsedBy, u.TraceId, u.ConditionSnapshotJson, u.CreatedAt,
            ExpectedEquipmentClassId = expectedEquipmentClassId,
        };
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (@"UPDATE EMS_TOOL SET
                 CURRENT_USE_COUNT=CURRENT_USE_COUNT + @UseCount,
                 CURRENT_USE_MINUTES=CURRENT_USE_MINUTES + @UseMinutes,
                 STATUS=CASE
                    WHEN (MAX_USE_COUNT IS NOT NULL AND CURRENT_USE_COUNT + @UseCount >= MAX_USE_COUNT)
                      OR (MAX_USE_MINUTES IS NOT NULL AND CURRENT_USE_MINUTES + @UseMinutes >= MAX_USE_MINUTES)
                    THEN 'Due' ELSE STATUS END,
                 UPDATED_BY=@UsedBy, UPDATED_AT=@UsedAt
               WHERE TOOL_ID=@ToolId AND IS_ACTIVE=1 AND STATUS IN ('Available','Mounted')
                  AND ((EQUIPMENT_CLASS_ID=@ExpectedEquipmentClassId)
                       OR (EQUIPMENT_CLASS_ID IS NULL AND @ExpectedEquipmentClassId IS NULL))
                  AND (MAX_USE_COUNT IS NULL OR CURRENT_USE_COUNT < MAX_USE_COUNT)
                  AND (MAX_USE_MINUTES IS NULL OR CURRENT_USE_MINUTES < MAX_USE_MINUTES)
                  AND (MAX_USE_COUNT IS NULL OR CURRENT_USE_COUNT + @UseCount <= MAX_USE_COUNT)
                  AND (MAX_USE_MINUTES IS NULL OR CURRENT_USE_MINUTES + @UseMinutes <= MAX_USE_MINUTES)
                  AND (NEXT_INSPECTION_DUE_AT IS NULL OR NEXT_INSPECTION_DUE_AT > @UsedAt)
                  AND (NEXT_CALIBRATION_DUE_AT IS NULL OR NEXT_CALIBRATION_DUE_AT > @UsedAt)
                  AND ((@MountId IS NULL AND STATUS='Available' AND NOT EXISTS (
                         SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY
                          WHERE TOOL_ID=@ToolId AND UNMOUNTED_AT IS NULL))
                       OR (@MountId IS NOT NULL AND STATUS='Mounted' AND EXISTS (
                         SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY
                          WHERE MOUNT_ID=@MountId AND TOOL_ID=@ToolId
                            AND EQUIPMENT_ID=@EquipmentId AND UNMOUNTED_AT IS NULL
                            AND @UsedAt >= MOUNTED_AT)))
                  AND NOT EXISTS (SELECT 1 FROM EMS_TOOL_USAGE_HISTORY WHERE IDEMPOTENCY_KEY=@IdempotencyKey)", p),
                (@"INSERT INTO EMS_TOOL_USAGE_HISTORY
               (USAGE_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, MOUNT_ID, EQUIPMENT_ID,
                PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION,
                USE_COUNT, USE_MINUTES, USED_AT, USED_BY, TRACE_ID, CONDITION_SNAPSHOT_JSON,
                CREATED_BY, CREATED_AT)
               VALUES
               (@UsageId, @IdempotencyKey, @RequestHash, @ToolId, @MountId, @EquipmentId,
                @ProcessLotId, @WorkOrderId, @ProcessId, @RecipeId, @RecipeVersion,
                @UseCount, @UseMinutes, @UsedAt, @UsedBy, @TraceId, @ConditionSnapshotJson,
                 @UsedBy, @CreatedAt)", p));
        }
        catch (DbException)
        {
            if (await GetUsageByIdempotencyKeyAsync(u.IdempotencyKey, ct) is not null) return false;
            throw;
        }
    }

    public async Task<ToolInspectionRecord?> GetInspectionByIdempotencyKeyAsync(string key, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<InspectionRow>(InspectionSelect + " WHERE IDEMPOTENCY_KEY=@key", new { key }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryRecordInspectionAsync(ToolInspectionRecord i, CancellationToken ct = default)
    {
        var p = new
        {
            i.InspectionId, i.IdempotencyKey, i.RequestHash, i.ToolId, i.InspectionType, i.Result,
            i.MeasuredValue, i.StandardValue, i.CertificateNumber, i.InspectedAt, i.InspectedBy,
            i.NextDueAt, i.Remark, i.CreatedAt,
        };
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (@"UPDATE EMS_TOOL SET
                 LAST_INSPECTED_AT=CASE
                    WHEN @InspectionType='Inspection'
                     AND (LAST_INSPECTED_AT IS NULL OR @InspectedAt > LAST_INSPECTED_AT)
                    THEN @InspectedAt ELSE LAST_INSPECTED_AT END,
                 LAST_CALIBRATED_AT=CASE
                    WHEN @InspectionType='Calibration'
                     AND (LAST_CALIBRATED_AT IS NULL OR @InspectedAt > LAST_CALIBRATED_AT)
                    THEN @InspectedAt ELSE LAST_CALIBRATED_AT END,
                 NEXT_INSPECTION_DUE_AT=CASE
                    WHEN @InspectionType='Inspection'
                     AND (LAST_INSPECTED_AT IS NULL OR @InspectedAt > LAST_INSPECTED_AT)
                    THEN @NextDueAt ELSE NEXT_INSPECTION_DUE_AT END,
                 NEXT_CALIBRATION_DUE_AT=CASE
                    WHEN @InspectionType='Calibration'
                     AND (LAST_CALIBRATED_AT IS NULL OR @InspectedAt > LAST_CALIBRATED_AT)
                    THEN @NextDueAt ELSE NEXT_CALIBRATION_DUE_AT END,
                 STATUS=CASE WHEN @Result='Fail' THEN 'Blocked' ELSE STATUS END,
                 UPDATED_BY=CASE
                    WHEN UPDATED_AT IS NULL OR @InspectedAt > UPDATED_AT THEN @InspectedBy
                    ELSE UPDATED_BY END,
                 UPDATED_AT=CASE
                    WHEN UPDATED_AT IS NULL OR @InspectedAt > UPDATED_AT THEN @InspectedAt
                    ELSE UPDATED_AT END
               WHERE TOOL_ID=@ToolId AND IS_ACTIVE=1
                  AND STATUS IN ('Available','Mounted','Due','Blocked')
                  AND NOT EXISTS (SELECT 1 FROM EMS_TOOL_INSPECTION_HISTORY WHERE IDEMPOTENCY_KEY=@IdempotencyKey)", p),
                (@"INSERT INTO EMS_TOOL_INSPECTION_HISTORY
               (INSPECTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, INSPECTION_TYPE, RESULT,
                MEASURED_VALUE, STANDARD_VALUE, CERTIFICATE_NO, INSPECTED_AT, INSPECTED_BY,
                NEXT_DUE_AT, REMARK, CREATED_BY, CREATED_AT)
               VALUES
               (@InspectionId, @IdempotencyKey, @RequestHash, @ToolId, @InspectionType, @Result,
                @MeasuredValue, @StandardValue, @CertificateNumber, @InspectedAt, @InspectedBy,
                 @NextDueAt, @Remark, @InspectedBy, @CreatedAt)", p));
        }
        catch (DbException)
        {
            if (await GetInspectionByIdempotencyKeyAsync(i.IdempotencyKey, ct) is not null) return false;
            throw;
        }
    }

    private const string ToolSelect = @"
        SELECT TOOL_ID AS ToolId, TOOL_NAME AS ToolName, TOOL_TYPE AS ToolType,
               TOOL_NUMBER AS ToolNumber, SERIAL_NO AS SerialNumber,
               EQUIPMENT_CLASS_ID AS EquipmentClassId, MAX_USE_COUNT AS MaxUseCount,
               MAX_USE_MINUTES AS MaxUseMinutes, CURRENT_USE_COUNT AS CurrentUseCount,
               CURRENT_USE_MINUTES AS CurrentUseMinutes, INSPECTION_CYCLE_DAYS AS InspectionCycleDays,
               CALIBRATION_CYCLE_DAYS AS CalibrationCycleDays, LAST_INSPECTED_AT AS LastInspectedAt,
               LAST_CALIBRATED_AT AS LastCalibratedAt, NEXT_INSPECTION_DUE_AT AS NextInspectionDueAt,
               NEXT_CALIBRATION_DUE_AT AS NextCalibrationDueAt, STATUS AS Status,
               LOCATION AS Location, IS_ACTIVE AS IsActive, VERSION_NO AS Version
        FROM EMS_TOOL";

    private const string ToolSaveCommandSelect = @"
        SELECT COMMAND_ID AS CommandId, IDEMPOTENCY_KEY AS IdempotencyKey,
               REQUEST_HASH AS RequestHash, TOOL_ID AS ToolId,
               EXPECTED_VERSION AS ExpectedVersion, RESULT_VERSION AS ResultVersion,
               RESULT_JSON AS ResultJson, ACTOR_ID AS ActorId, CREATED_AT AS CreatedAt
        FROM EMS_TOOL_SAVE_COMMAND";

    private const string MountSelect = @"
        SELECT MOUNT_ID AS MountId, IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
               TOOL_ID AS ToolId, EQUIPMENT_ID AS EquipmentId, POSITION_CODE AS PositionCode,
               MOUNTED_AT AS MountedAt, MOUNTED_BY AS MountedBy, UNMOUNTED_AT AS UnmountedAt,
               UNMOUNTED_BY AS UnmountedBy, UNMOUNT_IDEMPOTENCY_KEY AS UnmountIdempotencyKey,
               UNMOUNT_REQUEST_HASH AS UnmountRequestHash, UNMOUNT_REASON AS UnmountReason,
               CREATED_AT AS CreatedAt
        FROM EMS_TOOL_MOUNT_HISTORY";

    private const string UsageSelect = @"
        SELECT USAGE_ID AS UsageId, IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
               TOOL_ID AS ToolId, MOUNT_ID AS MountId, EQUIPMENT_ID AS EquipmentId,
               PROCESS_LOT_ID AS ProcessLotId, WORK_ORDER_ID AS WorkOrderId, PROCESS_ID AS ProcessId,
               RECIPE_ID AS RecipeId, RECIPE_VERSION AS RecipeVersion, USE_COUNT AS UseCount,
               USE_MINUTES AS UseMinutes, USED_AT AS UsedAt, USED_BY AS UsedBy, TRACE_ID AS TraceId,
               CONDITION_SNAPSHOT_JSON AS ConditionSnapshotJson, CREATED_AT AS CreatedAt
        FROM EMS_TOOL_USAGE_HISTORY";

    private const string InspectionSelect = @"
        SELECT INSPECTION_ID AS InspectionId, IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
               TOOL_ID AS ToolId, INSPECTION_TYPE AS InspectionType, RESULT AS Result,
               MEASURED_VALUE AS MeasuredValue, STANDARD_VALUE AS StandardValue,
               CERTIFICATE_NO AS CertificateNumber, INSPECTED_AT AS InspectedAt,
               INSPECTED_BY AS InspectedBy, NEXT_DUE_AT AS NextDueAt, REMARK AS Remark,
               CREATED_AT AS CreatedAt
        FROM EMS_TOOL_INSPECTION_HISTORY";

    private const string UpdateToolSql = @"
        UPDATE EMS_TOOL SET TOOL_NAME=@ToolName, TOOL_TYPE=@ToolType, TOOL_NUMBER=@ToolNumber,
          SERIAL_NO=@SerialNumber, EQUIPMENT_CLASS_ID=@EquipmentClassId, MAX_USE_COUNT=@MaxUseCount,
          MAX_USE_MINUTES=@MaxUseMinutes, INSPECTION_CYCLE_DAYS=@InspectionCycleDays,
          CALIBRATION_CYCLE_DAYS=@CalibrationCycleDays, STATUS=@Status, LOCATION=@Location,
          IS_ACTIVE=@IsActive, VERSION_NO=@Version, UPDATED_BY=@ActorId, UPDATED_AT=@Now
        WHERE TOOL_ID=@ToolId AND STATUS=@ExpectedStatus AND VERSION_NO=@ExpectedVersion
          AND NOT EXISTS (SELECT 1 FROM EMS_TOOL_SAVE_COMMAND WHERE IDEMPOTENCY_KEY=@IdempotencyKey)
          AND ((EXISTS (
                 SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY
                  WHERE TOOL_ID=@ToolId AND UNMOUNTED_AT IS NULL)
                AND @IsActive=1
                AND ((EQUIPMENT_CLASS_ID=@EquipmentClassId)
                     OR (EQUIPMENT_CLASS_ID IS NULL AND @EquipmentClassId IS NULL)))
               OR NOT EXISTS (
                 SELECT 1 FROM EMS_TOOL_MOUNT_HISTORY
                  WHERE TOOL_ID=@ToolId AND UNMOUNTED_AT IS NULL))";

    private const string InsertToolSql = @"
        INSERT INTO EMS_TOOL
        (TOOL_ID, TOOL_NAME, TOOL_TYPE, TOOL_NUMBER, SERIAL_NO, EQUIPMENT_CLASS_ID,
         MAX_USE_COUNT, MAX_USE_MINUTES, CURRENT_USE_COUNT, CURRENT_USE_MINUTES,
         INSPECTION_CYCLE_DAYS, CALIBRATION_CYCLE_DAYS, LAST_INSPECTED_AT, LAST_CALIBRATED_AT,
         NEXT_INSPECTION_DUE_AT, NEXT_CALIBRATION_DUE_AT, STATUS, LOCATION, IS_ACTIVE,
         VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @ToolId, @ToolName, @ToolType, @ToolNumber, @SerialNumber, @EquipmentClassId,
               @MaxUseCount, @MaxUseMinutes, @CurrentUseCount, @CurrentUseMinutes,
               @InspectionCycleDays, @CalibrationCycleDays, @LastInspectedAt, @LastCalibratedAt,
               @NextInspectionDueAt, @NextCalibrationDueAt, @Status, @Location, @IsActive,
               @Version, @ActorId, @Now, @ActorId, @Now
        WHERE @ExpectedVersion=0 AND @Status<>'Mounted'
          AND NOT EXISTS (SELECT 1 FROM EMS_TOOL_SAVE_COMMAND WHERE IDEMPOTENCY_KEY=@IdempotencyKey)
          AND NOT EXISTS (SELECT 1 FROM EMS_TOOL WHERE TOOL_ID=@ToolId)";

    private const string InsertToolSaveCommandSql = @"
        INSERT INTO EMS_TOOL_SAVE_COMMAND
            (COMMAND_ID, IDEMPOTENCY_KEY, REQUEST_HASH, TOOL_ID, EXPECTED_VERSION,
             RESULT_VERSION, RESULT_JSON, ACTOR_ID, CREATED_AT)
        VALUES
            (@CommandId, @IdempotencyKey, @RequestHash, @ToolId, @ExpectedVersion,
             @ResultVersion, @ResultJson, @ActorId, @Now)";

    private static bool IsUniqueViolation(DbException exception) => exception switch
    {
        Microsoft.Data.Sqlite.SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                                        && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when string.Equals(
                exception.GetType().FullName,
                "Microsoft.Data.SqlClient.SqlException",
                StringComparison.Ordinal)
            => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
               && number is 2601 or 2627,
        _ => false,
    };

    private sealed class ToolRow
    {
        public string ToolId { get; set; } = "";
        public string ToolName { get; set; } = "";
        public string ToolType { get; set; } = "";
        public string? ToolNumber { get; set; }
        public string? SerialNumber { get; set; }
        public string? EquipmentClassId { get; set; }
        public decimal? MaxUseCount { get; set; }
        public decimal? MaxUseMinutes { get; set; }
        public decimal CurrentUseCount { get; set; }
        public decimal CurrentUseMinutes { get; set; }
        public int? InspectionCycleDays { get; set; }
        public int? CalibrationCycleDays { get; set; }
        public DateTime? LastInspectedAt { get; set; }
        public DateTime? LastCalibratedAt { get; set; }
        public DateTime? NextInspectionDueAt { get; set; }
        public DateTime? NextCalibrationDueAt { get; set; }
        public string Status { get; set; } = "";
        public string? Location { get; set; }
        public bool IsActive { get; set; }
        public int Version { get; set; } = 1;
        public ToolRecord ToRecord() => new(
            ToolId, ToolName, ToolType, ToolNumber, SerialNumber, EquipmentClassId,
            MaxUseCount, MaxUseMinutes, CurrentUseCount, CurrentUseMinutes,
            InspectionCycleDays, CalibrationCycleDays, LastInspectedAt, LastCalibratedAt,
            NextInspectionDueAt, NextCalibrationDueAt, Status, Location, IsActive, Version);
    }

    private sealed class ToolSaveCommandRow
    {
        public string CommandId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ToolId { get; set; } = "";
        public int ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public string ResultJson { get; set; } = "";
        public string ActorId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public ToolSaveCommandRecord ToRecord() => new(
            CommandId, IdempotencyKey, RequestHash, ToolId, ExpectedVersion,
            ResultVersion, ResultJson, ActorId, CreatedAt);
    }

    private sealed class MountRow
    {
        public string MountId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ToolId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string? PositionCode { get; set; }
        public DateTime MountedAt { get; set; }
        public string MountedBy { get; set; } = "";
        public DateTime? UnmountedAt { get; set; }
        public string? UnmountedBy { get; set; }
        public string? UnmountIdempotencyKey { get; set; }
        public string? UnmountRequestHash { get; set; }
        public string? UnmountReason { get; set; }
        public DateTime CreatedAt { get; set; }
        public ToolMountRecord ToRecord() => new(
            MountId, IdempotencyKey, RequestHash, ToolId, EquipmentId, PositionCode,
            MountedAt, MountedBy, UnmountedAt, UnmountedBy, UnmountIdempotencyKey,
            UnmountRequestHash, UnmountReason, CreatedAt);
    }

    private sealed class UsageRow
    {
        public string UsageId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ToolId { get; set; } = "";
        public string? MountId { get; set; }
        public string EquipmentId { get; set; } = "";
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public decimal UseCount { get; set; }
        public decimal UseMinutes { get; set; }
        public DateTime UsedAt { get; set; }
        public string UsedBy { get; set; } = "";
        public string? TraceId { get; set; }
        public string? ConditionSnapshotJson { get; set; }
        public DateTime CreatedAt { get; set; }
        public ToolUsageRecord ToRecord() => new(
            UsageId, IdempotencyKey, RequestHash, ToolId, MountId, EquipmentId,
            ProcessLotId, WorkOrderId, ProcessId, RecipeId, RecipeVersion, UseCount,
            UseMinutes, UsedAt, UsedBy, TraceId, ConditionSnapshotJson, CreatedAt);
    }

    private sealed class LatestUsageRow
    {
        public DateTime? UsedAt { get; set; }
    }

    private sealed class InspectionRow
    {
        public string InspectionId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ToolId { get; set; } = "";
        public string InspectionType { get; set; } = "";
        public string Result { get; set; } = "";
        public string? MeasuredValue { get; set; }
        public string? StandardValue { get; set; }
        public string? CertificateNumber { get; set; }
        public DateTime InspectedAt { get; set; }
        public string InspectedBy { get; set; } = "";
        public DateTime? NextDueAt { get; set; }
        public string? Remark { get; set; }
        public DateTime CreatedAt { get; set; }
        public ToolInspectionRecord ToRecord() => new(
            InspectionId, IdempotencyKey, RequestHash, ToolId, InspectionType, Result,
            MeasuredValue, StandardValue, CertificateNumber, InspectedAt, InspectedBy,
            NextDueAt, Remark, CreatedAt);
    }
}
