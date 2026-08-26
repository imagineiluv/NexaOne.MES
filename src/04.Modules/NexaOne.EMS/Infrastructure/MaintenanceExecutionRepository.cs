using System.Data.Common;
using Microsoft.Data.Sqlite;
using NexaOne.EMS.Application.MaintenanceExecution;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EMS.Infrastructure;

/// <summary>
/// Persists manual maintenance evidence. Every write repeats the application guard in SQL so
/// concurrent work-order, mapping, and idempotency changes cannot create invalid history.
/// </summary>
public sealed class MaintenanceExecutionRepository : QueryRepository, IMaintenanceExecutionRepository
{
    private readonly ServiceObjectProcessor _processor;

    public MaintenanceExecutionRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public Task<string?> GetWorkOrderStatusAsync(
        string workOrderId,
        CancellationToken ct = default)
        => QueryFirstOrDefaultAsync<string>(
            "SELECT STATUS FROM EMS_WORK_ORDER WHERE WO_ID=@workOrderId",
            new { workOrderId }, ct);

    public async Task<bool> MaintenanceItemExistsAsync(
        string itemId,
        CancellationToken ct = default)
        => await CountAsync(
            "SELECT COUNT(*) FROM EMS_MAINT_ITEM WHERE ITEM_ID=@itemId",
            new { itemId }, ct) > 0;

    public Task<string?> GetActiveWorkerIdAsync(
        string userId,
        DateTime at,
        CancellationToken ct = default)
        => QueryFirstOrDefaultAsync<string>(@"
            SELECT WORKER_ID
              FROM MDM_WORKER_USER_MAP
             WHERE USER_ID=@userId AND IS_ACTIVE=1
               AND EFFECTIVE_FROM<=@at
               AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO>@at)",
            new { userId, at }, ct);

    public async Task<MaintenanceCheckRecord?> GetCheckByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<CheckRow>(
            CheckSelect + " WHERE IDEMPOTENCY_KEY=@idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryAddCheckAsync(
        MaintenanceCheckRecord record,
        CancellationToken ct = default)
    {
        var parameter = CheckParameter(record);
        try
        {
            return await _processor.ExecuteAsync(InsertCheckSql, parameter, ct) == 1;
        }
        catch (DbException exception) when (IsUniqueViolation(exception))
        {
            return false;
        }
    }

    public async Task<MaintenanceLaborRecord?> GetLaborAsync(
        string laborId,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<LaborRow>(
            LaborSelect + " WHERE LABOR_ID=@laborId", new { laborId }, ct);
        return row?.ToRecord();
    }

    public async Task<MaintenanceLaborRecord?> GetLaborByStartIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<LaborRow>(
            LaborSelect + " WHERE START_IDEMPOTENCY_KEY=@idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<MaintenanceLaborRecord?> GetLaborByEndIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<LaborRow>(
            LaborSelect + " WHERE END_IDEMPOTENCY_KEY=@idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryStartLaborAsync(
        MaintenanceLaborRecord record,
        CancellationToken ct = default)
    {
        var parameter = LaborParameter(record);
        try
        {
            return await _processor.ExecuteAsync(InsertLaborSql, parameter, ct) == 1;
        }
        catch (DbException exception) when (IsUniqueViolation(exception))
        {
            return false;
        }
    }

    public async Task<bool> TryCompleteLaborAsync(
        MaintenanceLaborRecord record,
        int expectedVersion,
        CancellationToken ct = default)
    {
        var parameter = new
        {
            record.LaborId,
            record.EndedAt,
            record.EndedBy,
            record.LaborHours,
            record.Remark,
            record.CorrelationId,
            record.EndIdempotencyKey,
            record.EndRequestHash,
            record.EndClientChannel,
            record.EndDeviceId,
            record.Version,
            record.UpdatedAt,
            ExpectedVersion = expectedVersion,
        };
        try
        {
            return await _processor.ExecuteAsync(CompleteLaborSql, parameter, ct) == 1;
        }
        catch (DbException exception) when (IsUniqueViolation(exception))
        {
            return false;
        }
    }

    private const string CheckSelect = @"
        SELECT CHECK_RESULT_ID AS CheckResultId,
               IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
               WO_ID AS WorkOrderId, ITEM_ID AS ItemId, ITEM_SEQUENCE AS ItemSequence,
               CHECK_NAME AS CheckName, MEASURED_VALUE AS MeasuredValue,
               ATTRIBUTE_VALUE AS AttributeValue, UNIT AS Unit, IS_PASS AS IsPass,
               FINDING AS Finding, RECORDED_BY AS RecordedBy, RECORDED_AT AS RecordedAt,
               CLIENT_CHANNEL AS ClientChannel, DEVICE_ID AS DeviceId,
               CORRELATION_ID AS CorrelationId, CREATED_AT AS CreatedAt
          FROM EMS_WORK_ORDER_CHECK_RESULT";

    private const string LaborSelect = @"
        SELECT LABOR_ID AS LaborId, START_IDEMPOTENCY_KEY AS StartIdempotencyKey,
               START_REQUEST_HASH AS StartRequestHash, WO_ID AS WorkOrderId,
               USER_ID AS UserId, WORKER_ID AS WorkerId, LABOR_TYPE AS LaborType,
               STARTED_AT AS StartedAt, ENDED_AT AS EndedAt, ENDED_BY AS EndedBy,
               LABOR_HOURS AS LaborHours, REMARK AS Remark,
               CORRELATION_ID AS CorrelationId,
               START_CLIENT_CHANNEL AS StartClientChannel,
               START_DEVICE_ID AS StartDeviceId,
               END_IDEMPOTENCY_KEY AS EndIdempotencyKey,
               END_REQUEST_HASH AS EndRequestHash,
               END_CLIENT_CHANNEL AS EndClientChannel,
               END_DEVICE_ID AS EndDeviceId, VERSION_NO AS Version,
               CREATED_AT AS CreatedAt, UPDATED_AT AS UpdatedAt
          FROM EMS_WORK_ORDER_LABOR";

    private const string InsertCheckSql = @"
        INSERT INTO EMS_WORK_ORDER_CHECK_RESULT
        (CHECK_RESULT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, WO_ID, ITEM_ID, ITEM_SEQUENCE,
         CHECK_NAME, MEASURED_VALUE, ATTRIBUTE_VALUE, UNIT, IS_PASS, FINDING,
         RECORDED_BY, RECORDED_AT, CLIENT_CHANNEL, DEVICE_ID, CORRELATION_ID,
         CREATED_BY, CREATED_AT)
        SELECT @CheckResultId, @IdempotencyKey, @RequestHash, @WorkOrderId, @ItemId,
               @ItemSequence, @CheckName, @MeasuredValue, @AttributeValue, @Unit, @IsPass,
               @Finding, @RecordedBy, @RecordedAt, @ClientChannel, @DeviceId,
               @CorrelationId, @RecordedBy, @CreatedAt
         WHERE EXISTS (
               SELECT 1 FROM EMS_WORK_ORDER
                WHERE WO_ID=@WorkOrderId AND STATUS='InProgress')
           AND (@ItemId IS NULL OR EXISTS (
               SELECT 1 FROM EMS_MAINT_ITEM WHERE ITEM_ID=@ItemId))
           AND NOT EXISTS (
               SELECT 1 FROM EMS_WORK_ORDER_CHECK_RESULT
                WHERE IDEMPOTENCY_KEY=@IdempotencyKey
                   OR CHECK_RESULT_ID=@CheckResultId
                   OR (WO_ID=@WorkOrderId AND ITEM_SEQUENCE=@ItemSequence))";

    private const string InsertLaborSql = @"
        INSERT INTO EMS_WORK_ORDER_LABOR
        (LABOR_ID, START_IDEMPOTENCY_KEY, START_REQUEST_HASH, WO_ID, USER_ID, WORKER_ID,
         LABOR_TYPE, STARTED_AT, REMARK, CORRELATION_ID,
         START_CLIENT_CHANNEL, START_DEVICE_ID, VERSION_NO,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @LaborId, @StartIdempotencyKey, @StartRequestHash, @WorkOrderId, @UserId,
               @WorkerId, @LaborType, @StartedAt, @Remark, @CorrelationId,
               @StartClientChannel, @StartDeviceId, @Version,
               @UserId, @CreatedAt, @UserId, @UpdatedAt
         WHERE EXISTS (
               SELECT 1 FROM EMS_WORK_ORDER
                WHERE WO_ID=@WorkOrderId AND STATUS='InProgress')
           AND EXISTS (SELECT 1 FROM SYS_USER WHERE USER_ID=@UserId)
           AND (@WorkerId IS NULL OR EXISTS (
               SELECT 1 FROM MDM_WORKER_USER_MAP
                WHERE USER_ID=@UserId AND WORKER_ID=@WorkerId AND IS_ACTIVE=1
                  AND EFFECTIVE_FROM<=@StartedAt
                  AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO>@StartedAt)))
           AND NOT EXISTS (
               SELECT 1 FROM EMS_WORK_ORDER_LABOR
                WHERE LABOR_ID=@LaborId OR START_IDEMPOTENCY_KEY=@StartIdempotencyKey
                   OR (WO_ID=@WorkOrderId AND USER_ID=@UserId AND ENDED_AT IS NULL))";

    private const string CompleteLaborSql = @"
        UPDATE EMS_WORK_ORDER_LABOR SET
               ENDED_AT=@EndedAt, ENDED_BY=@EndedBy, LABOR_HOURS=@LaborHours,
               REMARK=@Remark, CORRELATION_ID=@CorrelationId,
               END_IDEMPOTENCY_KEY=@EndIdempotencyKey,
               END_REQUEST_HASH=@EndRequestHash,
               END_CLIENT_CHANNEL=@EndClientChannel,
               END_DEVICE_ID=@EndDeviceId, VERSION_NO=@Version,
               UPDATED_BY=@EndedBy, UPDATED_AT=@UpdatedAt
         WHERE LABOR_ID=@LaborId AND VERSION_NO=@ExpectedVersion AND ENDED_AT IS NULL
           AND @EndedAt>=STARTED_AT
           AND NOT EXISTS (
               SELECT 1 FROM EMS_WORK_ORDER_LABOR
                WHERE END_IDEMPOTENCY_KEY=@EndIdempotencyKey)";

    private static object CheckParameter(MaintenanceCheckRecord record) => new
    {
        record.CheckResultId,
        record.IdempotencyKey,
        record.RequestHash,
        record.WorkOrderId,
        record.ItemId,
        record.ItemSequence,
        record.CheckName,
        record.MeasuredValue,
        record.AttributeValue,
        record.Unit,
        record.IsPass,
        record.Finding,
        record.RecordedBy,
        record.RecordedAt,
        record.ClientChannel,
        record.DeviceId,
        record.CorrelationId,
        record.CreatedAt,
    };

    private static object LaborParameter(MaintenanceLaborRecord record) => new
    {
        record.LaborId,
        record.StartIdempotencyKey,
        record.StartRequestHash,
        record.WorkOrderId,
        record.UserId,
        record.WorkerId,
        record.LaborType,
        record.StartedAt,
        record.Remark,
        record.CorrelationId,
        record.StartClientChannel,
        record.StartDeviceId,
        record.Version,
        record.CreatedAt,
        record.UpdatedAt,
    };

    private static bool IsUniqueViolation(DbException exception) => exception switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 19
                                  && sqlite.SqliteExtendedErrorCode is 1555 or 2067,
        _ when string.Equals(
                exception.GetType().FullName,
                "Microsoft.Data.SqlClient.SqlException",
                StringComparison.Ordinal)
            => exception.GetType().GetProperty("Number")?.GetValue(exception) is int number
               && number is 2601 or 2627,
        _ => false,
    };

    private sealed class CheckRow
    {
        public string CheckResultId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string WorkOrderId { get; set; } = "";
        public string? ItemId { get; set; }
        public int ItemSequence { get; set; }
        public string CheckName { get; set; } = "";
        public decimal? MeasuredValue { get; set; }
        public string? AttributeValue { get; set; }
        public string? Unit { get; set; }
        public bool? IsPass { get; set; }
        public string? Finding { get; set; }
        public string RecordedBy { get; set; } = "";
        public DateTime RecordedAt { get; set; }
        public string ClientChannel { get; set; } = "";
        public string? DeviceId { get; set; }
        public string? CorrelationId { get; set; }
        public DateTime CreatedAt { get; set; }

        public MaintenanceCheckRecord ToRecord() => new(
            CheckResultId, IdempotencyKey, RequestHash, WorkOrderId, ItemId,
            ItemSequence, CheckName, MeasuredValue, AttributeValue, Unit, IsPass,
            Finding, RecordedBy, RecordedAt, ClientChannel, DeviceId, CorrelationId,
            CreatedAt);
    }

    private sealed class LaborRow
    {
        public string LaborId { get; set; } = "";
        public string StartIdempotencyKey { get; set; } = "";
        public string StartRequestHash { get; set; } = "";
        public string WorkOrderId { get; set; } = "";
        public string UserId { get; set; } = "";
        public string? WorkerId { get; set; }
        public string LaborType { get; set; } = "";
        public DateTime StartedAt { get; set; }
        public DateTime? EndedAt { get; set; }
        public string? EndedBy { get; set; }
        public decimal? LaborHours { get; set; }
        public string? Remark { get; set; }
        public string? CorrelationId { get; set; }
        public string StartClientChannel { get; set; } = "";
        public string? StartDeviceId { get; set; }
        public string? EndIdempotencyKey { get; set; }
        public string? EndRequestHash { get; set; }
        public string? EndClientChannel { get; set; }
        public string? EndDeviceId { get; set; }
        public int Version { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime UpdatedAt { get; set; }

        public MaintenanceLaborRecord ToRecord() => new(
            LaborId, StartIdempotencyKey, StartRequestHash, WorkOrderId, UserId,
            WorkerId, LaborType, StartedAt, EndedAt, EndedBy, LaborHours, Remark,
            CorrelationId, StartClientChannel, StartDeviceId, EndIdempotencyKey,
            EndRequestHash, EndClientChannel, EndDeviceId, Version, CreatedAt, UpdatedAt);
    }
}
