using System.Data.Common;
using Microsoft.Data.Sqlite;
using NexaOne.EMS.Application.MaintenanceSchedules;
using NexaOne.Infrastructure.Persistence;

namespace NexaOne.EMS.Infrastructure;

public sealed class MaintenanceScheduleRepository : QueryRepository, IMaintenanceScheduleRepository
{
    private readonly ServiceObjectProcessor _processor;

    public MaintenanceScheduleRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<bool> MaintenancePlanExistsAsync(
        string maintenancePlanId,
        CancellationToken ct = default)
        => await CountAsync(
            "SELECT COUNT(*) FROM EMS_MAINTENANCE_PLAN WHERE PLAN_ID=@maintenancePlanId AND PLAN_TYPE='PM'",
            new { maintenancePlanId }, ct) > 0;

    public async Task<MaintenanceScheduleRecord?> GetAsync(
        string scheduleId,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ScheduleRow>(
            ScheduleSelect + " WHERE SCHEDULE_ID=@scheduleId", new { scheduleId }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryCreateAsync(
        MaintenanceScheduleRecord schedule,
        CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertScheduleSql, ScheduleParam(schedule)));
        }
        catch (DbException exception) when (IsScheduleIdentityRace(exception))
        {
            return false;
        }
    }

    public Task<bool> TryUpdateAsync(
        MaintenanceScheduleRecord schedule,
        int expectedVersion,
        CancellationToken ct = default)
        => _processor.ExecuteGuardedManyAsync(ct, (UpdateScheduleSql, ScheduleParam(schedule, expectedVersion)));

    public async Task<MaintenanceScheduleAcknowledgementRecord?> GetAcknowledgementAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<AcknowledgementRow>(
            AcknowledgementSelect + " WHERE IDEMPOTENCY_KEY=@idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryAcknowledgeAsync(
        MaintenanceScheduleRecord schedule,
        int expectedVersion,
        MaintenanceScheduleAcknowledgementRecord acknowledgement,
        CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (AcknowledgeScheduleSql, ScheduleParam(schedule, expectedVersion, acknowledgement.IdempotencyKey)),
                (InsertAcknowledgementSql, AcknowledgementParam(acknowledgement)));
        }
        catch (DbException exception) when (IsAcknowledgementIdempotencyRace(exception))
        {
            return false;
        }
    }

    private const string ScheduleSelect = @"
        SELECT SCHEDULE_ID AS ScheduleId, MAINTENANCE_PLAN_ID AS MaintenancePlanId,
               TRIGGER_TYPE AS TriggerType, INTERVAL_VALUE AS IntervalValue,
               INTERVAL_UNIT AS IntervalUnit, TIME_ZONE_ID AS TimeZoneId,
               LAST_DUE_AT AS LastDueAt, NEXT_DUE_AT AS NextDueAt,
               METER_PARAMETER_ID AS MeterParameterId, METER_THRESHOLD AS MeterThreshold,
               METER_BASELINE_VALUE AS MeterBaselineValue,
               NEXT_METER_DUE_VALUE AS NextMeterDueValue,
               CONDITION_RULE_ID AS ConditionRuleId, AUTO_CREATE_WO AS AutoCreateWorkOrder,
               IS_ACTIVE AS IsActive, VERSION_NO AS Version,
               CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt,
               UPDATED_BY AS UpdatedBy, UPDATED_AT AS UpdatedAt
        FROM EMS_MAINTENANCE_SCHEDULE";

    private const string AcknowledgementSelect = @"
        SELECT ACK_ID AS AcknowledgementId, SCHEDULE_ID AS ScheduleId,
               MAINTENANCE_PLAN_ID AS MaintenancePlanId, TRIGGER_TYPE AS TriggerType,
               DUE_AT AS DueAt, NEXT_DUE_AT AS NextDueAt,
               METER_DUE_VALUE AS MeterDueValue, OBSERVED_METER_VALUE AS ObservedMeterValue,
               NEXT_METER_DUE_VALUE AS NextMeterDueValue,
               CONDITION_RULE_ID AS ConditionRuleId, CONDITION_MET AS ConditionMet,
               ACKNOWLEDGED_AT AS AcknowledgedAt, ACKNOWLEDGED_BY AS AcknowledgedBy,
               REMARK AS Remark, IDEMPOTENCY_KEY AS IdempotencyKey,
               REQUEST_HASH AS RequestHash, CLIENT_CHANNEL AS ClientChannel,
               DEVICE_ID AS DeviceId, CORRELATION_ID AS CorrelationId,
               FROM_VERSION AS FromVersion, TO_VERSION AS ToVersion,
               CREATED_AT AS CreatedAt
        FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY";

    private const string InsertScheduleSql = @"
        INSERT INTO EMS_MAINTENANCE_SCHEDULE
        (SCHEDULE_ID, MAINTENANCE_PLAN_ID, TRIGGER_TYPE, INTERVAL_VALUE, INTERVAL_UNIT,
         TIME_ZONE_ID, LAST_DUE_AT, NEXT_DUE_AT, METER_PARAMETER_ID, METER_THRESHOLD,
         METER_BASELINE_VALUE, NEXT_METER_DUE_VALUE, CONDITION_RULE_ID, AUTO_CREATE_WO,
         IS_ACTIVE, VERSION_NO, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @ScheduleId, @MaintenancePlanId, @TriggerType, @IntervalValue, @IntervalUnit,
               @TimeZoneId, @LastDueAt, @NextDueAt, @MeterParameterId, @MeterThreshold,
               @MeterBaselineValue, @NextMeterDueValue, @ConditionRuleId, @AutoCreateWorkOrder,
               @IsActive, @Version, @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt
        WHERE EXISTS (
            SELECT 1 FROM EMS_MAINTENANCE_PLAN
             WHERE PLAN_ID=@MaintenancePlanId AND PLAN_TYPE='PM')
          AND NOT EXISTS (
            SELECT 1 FROM EMS_MAINTENANCE_SCHEDULE
             WHERE SCHEDULE_ID=@ScheduleId OR MAINTENANCE_PLAN_ID=@MaintenancePlanId)";

    private const string UpdateScheduleSql = @"
        UPDATE EMS_MAINTENANCE_SCHEDULE SET
            MAINTENANCE_PLAN_ID=@MaintenancePlanId, TRIGGER_TYPE=@TriggerType,
            INTERVAL_VALUE=@IntervalValue, INTERVAL_UNIT=@IntervalUnit,
            TIME_ZONE_ID=@TimeZoneId, NEXT_DUE_AT=@NextDueAt,
            METER_PARAMETER_ID=@MeterParameterId, METER_THRESHOLD=@MeterThreshold,
            METER_BASELINE_VALUE=@MeterBaselineValue,
            NEXT_METER_DUE_VALUE=@NextMeterDueValue, CONDITION_RULE_ID=@ConditionRuleId,
            AUTO_CREATE_WO=@AutoCreateWorkOrder, IS_ACTIVE=@IsActive,
            VERSION_NO=@Version, UPDATED_BY=@UpdatedBy, UPDATED_AT=@UpdatedAt
        WHERE SCHEDULE_ID=@ScheduleId AND VERSION_NO=@ExpectedVersion
          AND EXISTS (SELECT 1 FROM EMS_MAINTENANCE_PLAN
                       WHERE PLAN_ID=@MaintenancePlanId AND PLAN_TYPE='PM')
          AND NOT EXISTS (
              SELECT 1 FROM EMS_MAINTENANCE_SCHEDULE
               WHERE MAINTENANCE_PLAN_ID=@MaintenancePlanId AND SCHEDULE_ID<>@ScheduleId)";

    private const string AcknowledgeScheduleSql = @"
        UPDATE EMS_MAINTENANCE_SCHEDULE SET
            LAST_DUE_AT=@LastDueAt, NEXT_DUE_AT=@NextDueAt,
            METER_BASELINE_VALUE=@MeterBaselineValue,
            NEXT_METER_DUE_VALUE=@NextMeterDueValue,
            VERSION_NO=@Version, UPDATED_BY=@UpdatedBy, UPDATED_AT=@UpdatedAt
        WHERE SCHEDULE_ID=@ScheduleId AND VERSION_NO=@ExpectedVersion AND IS_ACTIVE=1
          AND EXISTS (SELECT 1 FROM EMS_MAINTENANCE_PLAN
                       WHERE PLAN_ID=@MaintenancePlanId AND PLAN_TYPE='PM')
          AND NOT EXISTS (
              SELECT 1 FROM EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY
               WHERE IDEMPOTENCY_KEY=@IdempotencyKey)";

    private const string InsertAcknowledgementSql = @"
        INSERT INTO EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY
        (ACK_ID, SCHEDULE_ID, MAINTENANCE_PLAN_ID, TRIGGER_TYPE, DUE_AT, NEXT_DUE_AT,
         METER_DUE_VALUE, OBSERVED_METER_VALUE, NEXT_METER_DUE_VALUE,
         CONDITION_RULE_ID, CONDITION_MET, ACKNOWLEDGED_AT, ACKNOWLEDGED_BY, REMARK,
         IDEMPOTENCY_KEY, REQUEST_HASH, CLIENT_CHANNEL, DEVICE_ID, CORRELATION_ID,
         FROM_VERSION, TO_VERSION, CREATED_BY, CREATED_AT)
        VALUES
        (@AcknowledgementId, @ScheduleId, @MaintenancePlanId, @TriggerType, @DueAt, @NextDueAt,
         @MeterDueValue, @ObservedMeterValue, @NextMeterDueValue,
         @ConditionRuleId, @ConditionMet, @AcknowledgedAt, @AcknowledgedBy, @Remark,
         @IdempotencyKey, @RequestHash, @ClientChannel, @DeviceId, @CorrelationId,
         @FromVersion, @ToVersion, @AcknowledgedBy, @CreatedAt)";

    private static object ScheduleParam(
        MaintenanceScheduleRecord schedule,
        int? expectedVersion = null,
        string? idempotencyKey = null) => new
    {
        schedule.ScheduleId,
        schedule.MaintenancePlanId,
        schedule.TriggerType,
        schedule.IntervalValue,
        schedule.IntervalUnit,
        schedule.TimeZoneId,
        schedule.LastDueAt,
        schedule.NextDueAt,
        schedule.MeterParameterId,
        schedule.MeterThreshold,
        schedule.MeterBaselineValue,
        schedule.NextMeterDueValue,
        schedule.ConditionRuleId,
        schedule.AutoCreateWorkOrder,
        schedule.IsActive,
        schedule.Version,
        schedule.CreatedBy,
        schedule.CreatedAt,
        schedule.UpdatedBy,
        schedule.UpdatedAt,
        ExpectedVersion = expectedVersion,
        IdempotencyKey = idempotencyKey,
    };

    private static object AcknowledgementParam(MaintenanceScheduleAcknowledgementRecord item) => new
    {
        item.AcknowledgementId,
        item.ScheduleId,
        item.MaintenancePlanId,
        item.TriggerType,
        item.DueAt,
        item.NextDueAt,
        item.MeterDueValue,
        item.ObservedMeterValue,
        item.NextMeterDueValue,
        item.ConditionRuleId,
        item.ConditionMet,
        item.AcknowledgedAt,
        item.AcknowledgedBy,
        item.Remark,
        item.IdempotencyKey,
        item.RequestHash,
        item.ClientChannel,
        item.DeviceId,
        item.CorrelationId,
        item.FromVersion,
        item.ToVersion,
        item.CreatedAt,
    };

    private static bool IsScheduleIdentityRace(DbException exception)
        => IsUniqueViolation(exception)
           && (exception.Message.Contains("PK_EMS_MAINTENANCE_SCHEDULE", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("UQ_EMS_MAINTENANCE_SCHEDULE_PLAN", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("EMS_MAINTENANCE_SCHEDULE.SCHEDULE_ID", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains("EMS_MAINTENANCE_SCHEDULE.MAINTENANCE_PLAN_ID", StringComparison.OrdinalIgnoreCase));

    private static bool IsAcknowledgementIdempotencyRace(DbException exception)
        => IsUniqueViolation(exception)
           && (exception.Message.Contains("UQ_EMS_MAINT_SCHEDULE_ACK_IDEMPOTENCY", StringComparison.OrdinalIgnoreCase)
               || exception.Message.Contains(
                   "EMS_MAINTENANCE_SCHEDULE_ACK_HISTORY.IDEMPOTENCY_KEY",
                   StringComparison.OrdinalIgnoreCase));

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

    private sealed class ScheduleRow
    {
        public string ScheduleId { get; set; } = "";
        public string MaintenancePlanId { get; set; } = "";
        public string TriggerType { get; set; } = "";
        public decimal? IntervalValue { get; set; }
        public string? IntervalUnit { get; set; }
        public string TimeZoneId { get; set; } = "";
        public DateTime? LastDueAt { get; set; }
        public DateTime? NextDueAt { get; set; }
        public string? MeterParameterId { get; set; }
        public decimal? MeterThreshold { get; set; }
        public decimal? MeterBaselineValue { get; set; }
        public decimal? NextMeterDueValue { get; set; }
        public string? ConditionRuleId { get; set; }
        public bool AutoCreateWorkOrder { get; set; }
        public bool IsActive { get; set; }
        public int Version { get; set; }
        public string CreatedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; } = "";
        public DateTime UpdatedAt { get; set; }

        public MaintenanceScheduleRecord ToRecord() => new(
            ScheduleId, MaintenancePlanId, TriggerType, IntervalValue, IntervalUnit,
            TimeZoneId, LastDueAt, NextDueAt, MeterParameterId, MeterThreshold,
            MeterBaselineValue, NextMeterDueValue, ConditionRuleId, AutoCreateWorkOrder,
            IsActive, Version, CreatedBy, CreatedAt, UpdatedBy, UpdatedAt);
    }

    private sealed class AcknowledgementRow
    {
        public string AcknowledgementId { get; set; } = "";
        public string ScheduleId { get; set; } = "";
        public string MaintenancePlanId { get; set; } = "";
        public string TriggerType { get; set; } = "";
        public DateTime? DueAt { get; set; }
        public DateTime? NextDueAt { get; set; }
        public decimal? MeterDueValue { get; set; }
        public decimal? ObservedMeterValue { get; set; }
        public decimal? NextMeterDueValue { get; set; }
        public string? ConditionRuleId { get; set; }
        public bool? ConditionMet { get; set; }
        public DateTime AcknowledgedAt { get; set; }
        public string AcknowledgedBy { get; set; } = "";
        public string? Remark { get; set; }
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string ClientChannel { get; set; } = "";
        public string? DeviceId { get; set; }
        public string? CorrelationId { get; set; }
        public int FromVersion { get; set; }
        public int ToVersion { get; set; }
        public DateTime CreatedAt { get; set; }

        public MaintenanceScheduleAcknowledgementRecord ToRecord() => new(
            AcknowledgementId, ScheduleId, MaintenancePlanId, TriggerType, DueAt, NextDueAt,
            MeterDueValue, ObservedMeterValue, NextMeterDueValue, ConditionRuleId, ConditionMet,
            AcknowledgedAt, AcknowledgedBy, Remark, IdempotencyKey, RequestHash, ClientChannel,
            DeviceId, CorrelationId, FromVersion, ToVersion, CreatedAt);
    }
}
