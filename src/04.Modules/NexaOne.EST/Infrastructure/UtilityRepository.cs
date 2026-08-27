using NexaOne.EST.Application.Est;
using NexaOne.Infrastructure.Persistence;
using NexaDB.Data.Abstractions.Interfaces;
using System.Data.Common;

namespace NexaOne.EST.Infrastructure;

public sealed class UtilityRepository : QueryRepository, IUtilityRepository
{
    private readonly ServiceObjectProcessor _processor;
    private readonly INexaOneEESDbCapability _dialect;

    public UtilityRepository(EesDataSource dataSource, INexaOneEESDbCapability dialect) : base(dataSource)
    {
        _processor = new ServiceObjectProcessor(dataSource);
        _dialect = dialect;
    }

    public async Task<UtilityMeterRecord?> GetMeterAsync(string meterId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MeterRow>(MeterSelect + " WHERE METER_ID = @meterId", new { meterId }, ct);
        return row?.ToRecord();
    }

    public async Task<UtilityMeterSaveCommandRecord?> GetMeterSaveCommandAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MeterSaveCommandRow>(
            @"SELECT IDEMPOTENCY_KEY AS IdempotencyKey, REQUEST_HASH AS RequestHash,
                     METER_ID AS MeterId, EXPECTED_VERSION AS ExpectedVersion,
                     RESULT_VERSION AS ResultVersion, ACTOR_ID AS ActorId, CREATED_AT AS CreatedAt
                FROM EST_UTILITY_METER_SAVE_COMMAND
               WHERE IDEMPOTENCY_KEY=@idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TrySaveMeterAsync(
        UtilityMeterRecord m,
        int expectedVersion,
        string idempotencyKey,
        string requestHash,
        string actorId,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var param = new
        {
            m.MeterId, m.MeterName, m.PlantId, m.EquipmentId, m.UtilityType, m.Unit,
            m.FdcParameterId, m.ReadingMode, m.ScaleFactor, m.CostPerUnit, m.CarbonPerUnit,
            m.IsActive, m.ConfigVersion, ExpectedVersion = expectedVersion,
            ActorId = actorId, Now = now,
        };
        var guardSql = expectedVersion == 0 ? InsertMeterSql : UpdateMeterSql;
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (guardSql, param),
                (InsertMeterConfigHistorySql, new
                {
                    HistoryId = $"UMH_{Guid.NewGuid():N}",
                    m.MeterId, m.ConfigVersion, m.MeterName, m.PlantId, m.EquipmentId,
                    m.UtilityType, m.Unit, m.FdcParameterId, m.ReadingMode, m.ScaleFactor,
                    m.CostPerUnit, m.CarbonPerUnit, m.IsActive,
                    ChangedBy = actorId, ChangedAt = now,
                }),
                (InsertMeterSaveCommandSql, new
                {
                    IdempotencyKey = idempotencyKey,
                    RequestHash = requestHash,
                    m.MeterId,
                    ExpectedVersion = expectedVersion,
                    ResultVersion = m.ConfigVersion,
                    ActorId = actorId,
                    CreatedAt = now,
                }));
        }
        catch (DbException)
        {
            if (await GetMeterSaveCommandAsync(idempotencyKey, ct) is not null) return false;
            throw;
        }
    }

    public async Task<IReadOnlyList<UtilityMeterConfigHistoryRecord>> GetMeterConfigHistoryAsync(
        string meterId, CancellationToken ct = default)
    {
        var rows = await QueryAsync<MeterConfigHistoryRow>(
            MeterConfigHistorySelect + " WHERE METER_ID = @meterId ORDER BY CONFIG_VERSION, CHANGED_AT, HISTORY_ID",
            new { meterId }, ct);
        return rows.Select(row => row.ToRecord()).ToList();
    }

    public async Task<UtilityReadingRecord?> GetReadingAsync(
        string source, string sourceEventId, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<ReadingRow>(
            ReadingSelect + " WHERE SOURCE = @source AND SOURCE_EVENT_ID = @sourceEventId",
            new { source, sourceEventId }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryAddReadingAsync(UtilityReadingRecord r, CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertReadingSql, new
            {
                r.ReadingId, r.MeterId, r.EquipmentId, r.ProcessLotId, r.WorkOrderId, r.RecipeId,
                r.RecipeVersion, r.RawValue, r.NormalizedValue, r.Unit, r.Source, r.SourceEventId,
                r.RequestHash, r.Quality, r.RecordedAt, r.RecordedBy, r.CreatedAt,
                r.MeterConfigVersion, r.PlantId, r.ReadingMode, r.CostPerUnit, r.CarbonPerUnit,
            }));
        }
        catch (DbException)
        {
            if (await GetReadingAsync(r.Source, r.SourceEventId, ct) is not null) return false;
            throw;
        }
    }

    public async Task<UtilityMeterEventRecord?> GetMeterEventAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<MeterEventRow>(
            MeterEventSelect + " WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryAddMeterEventAsync(UtilityMeterEventRecord e, CancellationToken ct = default)
    {
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertMeterEventSql, new
            {
                e.EventId, e.IdempotencyKey, e.RequestHash, e.MeterId, e.PlantId, e.EquipmentId,
                e.EventType, e.OccurredAt, e.Reason, e.PreviousValue, e.AfterValue,
                e.BaselineValue, e.Unit, e.ActorUserId, e.CreatedAt, e.MeterConfigVersion,
            }));
        }
        catch (DbException)
        {
            if (await GetMeterEventAsync(e.IdempotencyKey, ct) is not null) return false;
            throw;
        }
    }

    public async Task<IReadOnlyList<UtilityMeterEventRecord>> GetMeterEventsAsync(
        string meterId, DateTime fromInclusive, DateTime toExclusive, CancellationToken ct = default)
        => (await QueryAsync<MeterEventRow>(
                MeterEventSelect + @" WHERE METER_ID = @meterId
                                      AND OCCURRED_AT >= @fromInclusive AND OCCURRED_AT < @toExclusive
                                      ORDER BY OCCURRED_AT, CREATED_AT, EVENT_ID",
                new { meterId, fromInclusive, toExclusive }, ct))
            .Select(row => row.ToRecord())
            .ToList();

    public async Task<IReadOnlyList<UtilityReadingRecord>> GetPeriodReadingsAsync(
        string meterId, DateTime from, DateTime to, bool includeBaseline, CancellationToken ct = default)
    {
        var rows = (await QueryAsync<ReadingRow>(
            ReadingSelect + @" WHERE METER_ID = @meterId
                               AND RECORDED_AT >= @from AND RECORDED_AT < @to
                               ORDER BY RECORDED_AT",
            new { meterId, from, to }, ct)).Select(r => r.ToRecord()).ToList();
        if (!includeBaseline) return rows;

        var baselineSql = _dialect.WrapPaged(
            ReadingSelect + @" WHERE METER_ID = @meterId
                                AND RECORDED_AT <= @from
                                AND UPPER(QUALITY) = 'GOOD'",
            "RECORDED_AT DESC, CREATED_AT DESC, READING_ID DESC", 0, 1);
        var baseline = (await QueryAsync<ReadingRow>(baselineSql, new { meterId, from }, ct))
            .Select(r => r.ToRecord()).FirstOrDefault();
        if (baseline is not null && rows.All(r => r.ReadingId != baseline.ReadingId)) rows.Insert(0, baseline);
        return rows;
    }

    public Task SaveSummaryAsync(UtilitySummaryRecord s, string actorId, CancellationToken ct = default)
        => _processor.ExecuteManyAsync(ct,
            ("DELETE FROM EST_UTILITY_SUMMARY WHERE SUMMARY_ID = @SummaryId", new { s.SummaryId }),
            (InsertSummarySql, new
            {
                s.SummaryId, s.MeterId, s.PlantId, s.EquipmentId, s.PeriodType, s.PeriodStart,
                s.PeriodEnd, s.StartReading, s.EndReading, s.Consumption, s.Unit, s.CostAmount,
                s.CarbonAmount, ActorId = actorId, s.CreatedAt,
            }));

    private const string MeterSelect = @"
        SELECT METER_ID AS MeterId, METER_NAME AS MeterName, PLANT_ID AS PlantId,
               EQUIPMENT_ID AS EquipmentId, UTILITY_TYPE AS UtilityType, UNIT AS Unit,
               FDC_PARAMETER_ID AS FdcParameterId, READING_MODE AS ReadingMode,
               SCALE_FACTOR AS ScaleFactor, COST_PER_UNIT AS CostPerUnit,
               CARBON_PER_UNIT AS CarbonPerUnit, IS_ACTIVE AS IsActive,
               CONFIG_VERSION AS ConfigVersion
        FROM EST_UTILITY_METER";

    private const string ReadingSelect = @"
        SELECT READING_ID AS ReadingId, METER_ID AS MeterId, EQUIPMENT_ID AS EquipmentId,
               PROCESS_LOT_ID AS ProcessLotId, WORK_ORDER_ID AS WorkOrderId, RECIPE_ID AS RecipeId,
               RECIPE_VERSION AS RecipeVersion, RAW_VALUE AS RawValue,
               NORMALIZED_VALUE AS NormalizedValue, UNIT AS Unit, SOURCE AS Source,
               SOURCE_EVENT_ID AS SourceEventId, REQUEST_HASH AS RequestHash, QUALITY AS Quality,
               RECORDED_AT AS RecordedAt, RECORDED_BY AS RecordedBy, CREATED_AT AS CreatedAt,
               METER_CONFIG_VERSION AS MeterConfigVersion, PLANT_ID AS PlantId,
               READING_MODE AS ReadingMode, COST_PER_UNIT AS CostPerUnit,
               CARBON_PER_UNIT AS CarbonPerUnit
        FROM EST_UTILITY_READING";

    private const string MeterEventSelect = @"
        SELECT EVENT_ID AS EventId, IDEMPOTENCY_KEY AS IdempotencyKey,
               REQUEST_HASH AS RequestHash, METER_ID AS MeterId, PLANT_ID AS PlantId,
               EQUIPMENT_ID AS EquipmentId, EVENT_TYPE AS EventType,
               OCCURRED_AT AS OccurredAt, REASON AS Reason,
               PREVIOUS_VALUE AS PreviousValue, AFTER_VALUE AS AfterValue,
               BASELINE_VALUE AS BaselineValue, UNIT AS Unit,
               ACTOR_USER_ID AS ActorUserId, CREATED_AT AS CreatedAt,
               METER_CONFIG_VERSION AS MeterConfigVersion
        FROM EST_UTILITY_METER_EVENT";

    private const string MeterConfigHistorySelect = @"
        SELECT HISTORY_ID AS HistoryId, METER_ID AS MeterId, CONFIG_VERSION AS ConfigVersion,
               METER_NAME AS MeterName, PLANT_ID AS PlantId, EQUIPMENT_ID AS EquipmentId,
               UTILITY_TYPE AS UtilityType, UNIT AS Unit, FDC_PARAMETER_ID AS FdcParameterId,
               READING_MODE AS ReadingMode, SCALE_FACTOR AS ScaleFactor,
               COST_PER_UNIT AS CostPerUnit, CARBON_PER_UNIT AS CarbonPerUnit,
               IS_ACTIVE AS IsActive, CHANGED_BY AS ChangedBy, CHANGED_AT AS ChangedAt
        FROM EST_UTILITY_METER_CONFIG_HISTORY";

    private const string UpdateMeterSql = @"
        UPDATE EST_UTILITY_METER SET
            METER_NAME=@MeterName, PLANT_ID=@PlantId, EQUIPMENT_ID=@EquipmentId,
            UTILITY_TYPE=@UtilityType, UNIT=@Unit, FDC_PARAMETER_ID=@FdcParameterId,
            READING_MODE=@ReadingMode, SCALE_FACTOR=@ScaleFactor, COST_PER_UNIT=@CostPerUnit,
            CARBON_PER_UNIT=@CarbonPerUnit, IS_ACTIVE=@IsActive, CONFIG_VERSION=@ConfigVersion,
            UPDATED_BY=@ActorId, UPDATED_AT=@Now
        WHERE METER_ID=@MeterId AND CONFIG_VERSION=@ExpectedVersion
          AND @ConfigVersion = @ExpectedVersion + 1";

    private const string InsertMeterSql = @"
        INSERT INTO EST_UTILITY_METER
        (METER_ID, METER_NAME, PLANT_ID, EQUIPMENT_ID, UTILITY_TYPE, UNIT, FDC_PARAMETER_ID,
         READING_MODE, SCALE_FACTOR, COST_PER_UNIT, CARBON_PER_UNIT, IS_ACTIVE,
         CONFIG_VERSION, CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @MeterId, @MeterName, @PlantId, @EquipmentId, @UtilityType, @Unit, @FdcParameterId,
               @ReadingMode, @ScaleFactor, @CostPerUnit, @CarbonPerUnit, @IsActive,
               @ConfigVersion, @ActorId, @Now, @ActorId, @Now
        WHERE @ExpectedVersion = 0 AND @ConfigVersion = 1
          AND NOT EXISTS (SELECT 1 FROM EST_UTILITY_METER WHERE METER_ID=@MeterId)";

    private const string InsertMeterConfigHistorySql = @"
        INSERT INTO EST_UTILITY_METER_CONFIG_HISTORY
        (HISTORY_ID, METER_ID, CONFIG_VERSION, METER_NAME, PLANT_ID, EQUIPMENT_ID,
         UTILITY_TYPE, UNIT, FDC_PARAMETER_ID, READING_MODE, SCALE_FACTOR,
         COST_PER_UNIT, CARBON_PER_UNIT, IS_ACTIVE, CHANGED_BY, CHANGED_AT)
        VALUES
        (@HistoryId, @MeterId, @ConfigVersion, @MeterName, @PlantId, @EquipmentId,
         @UtilityType, @Unit, @FdcParameterId, @ReadingMode, @ScaleFactor,
          @CostPerUnit, @CarbonPerUnit, @IsActive, @ChangedBy, @ChangedAt)";

    private const string InsertMeterSaveCommandSql = @"
        INSERT INTO EST_UTILITY_METER_SAVE_COMMAND
        (IDEMPOTENCY_KEY, REQUEST_HASH, METER_ID, EXPECTED_VERSION, RESULT_VERSION,
         ACTOR_ID, CREATED_AT)
        VALUES
        (@IdempotencyKey, @RequestHash, @MeterId, @ExpectedVersion, @ResultVersion,
         @ActorId, @CreatedAt)";

    private const string InsertReadingSql = @"
        INSERT INTO EST_UTILITY_READING
        (READING_ID, METER_ID, EQUIPMENT_ID, PROCESS_LOT_ID, WORK_ORDER_ID, RECIPE_ID,
         RECIPE_VERSION, RAW_VALUE, NORMALIZED_VALUE, UNIT, SOURCE, SOURCE_EVENT_ID,
         REQUEST_HASH, QUALITY, RECORDED_AT, RECORDED_BY, CREATED_AT,
         METER_CONFIG_VERSION, PLANT_ID, READING_MODE, COST_PER_UNIT, CARBON_PER_UNIT)
        SELECT @ReadingId, @MeterId, @EquipmentId, @ProcessLotId, @WorkOrderId, @RecipeId,
               @RecipeVersion, @RawValue, @NormalizedValue, @Unit, @Source, @SourceEventId,
               @RequestHash, @Quality, @RecordedAt, @RecordedBy, @CreatedAt,
               @MeterConfigVersion, @PlantId, @ReadingMode, @CostPerUnit, @CarbonPerUnit
        FROM EST_UTILITY_METER M
        WHERE M.METER_ID = @MeterId
          AND M.IS_ACTIVE = 1
          AND M.CONFIG_VERSION = @MeterConfigVersion
          AND NOT EXISTS (
            SELECT 1 FROM EST_UTILITY_READING
            WHERE SOURCE=@Source AND SOURCE_EVENT_ID=@SourceEventId
        )";

    // INSERT ... SELECT is the atomic master-state guard. A service-only active/mode check can be
    // bypassed or race a meter update; this statement persists history only while the same cumulative
    // meter assignment and unit are still active. The unique key supplies the concurrent idempotency guard.
    private const string InsertMeterEventSql = @"
        INSERT INTO EST_UTILITY_METER_EVENT
        (EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, METER_ID, PLANT_ID, EQUIPMENT_ID,
         EVENT_TYPE, OCCURRED_AT, REASON, PREVIOUS_VALUE, AFTER_VALUE, BASELINE_VALUE,
         UNIT, ACTOR_USER_ID, CREATED_AT, METER_CONFIG_VERSION)
        SELECT @EventId, @IdempotencyKey, @RequestHash, M.METER_ID, @PlantId, @EquipmentId,
               @EventType, @OccurredAt, @Reason, @PreviousValue, @AfterValue, @BaselineValue,
               @Unit, @ActorUserId, @CreatedAt, @MeterConfigVersion
        FROM EST_UTILITY_METER M
        WHERE M.METER_ID = @MeterId
          AND M.IS_ACTIVE = 1
          AND M.CONFIG_VERSION = @MeterConfigVersion
          AND UPPER(M.READING_MODE) = 'CUMULATIVE'
          AND M.PLANT_ID = @PlantId
          AND M.UNIT = @Unit
          AND (M.EQUIPMENT_ID = @EquipmentId
               OR (M.EQUIPMENT_ID IS NULL AND @EquipmentId IS NULL))
          AND NOT EXISTS (
              SELECT 1 FROM EST_UTILITY_METER_EVENT E
              WHERE E.IDEMPOTENCY_KEY = @IdempotencyKey
          )";

    private const string InsertSummarySql = @"
        INSERT INTO EST_UTILITY_SUMMARY
        (SUMMARY_ID, METER_ID, PLANT_ID, EQUIPMENT_ID, PERIOD_TYPE, PERIOD_START, PERIOD_END,
         START_READING, END_READING, CONSUMPTION, UNIT, COST_AMOUNT, CARBON_AMOUNT,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
        (@SummaryId, @MeterId, @PlantId, @EquipmentId, @PeriodType, @PeriodStart, @PeriodEnd,
         @StartReading, @EndReading, @Consumption, @Unit, @CostAmount, @CarbonAmount,
         @ActorId, @CreatedAt, @ActorId, @CreatedAt)";

    private sealed class MeterRow
    {
        public string MeterId { get; set; } = "";
        public string MeterName { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string? EquipmentId { get; set; }
        public string UtilityType { get; set; } = "";
        public string Unit { get; set; } = "";
        public string? FdcParameterId { get; set; }
        public string ReadingMode { get; set; } = "Cumulative";
        public decimal ScaleFactor { get; set; }
        public decimal? CostPerUnit { get; set; }
        public decimal? CarbonPerUnit { get; set; }
        public bool IsActive { get; set; }
        public int ConfigVersion { get; set; } = 1;

        public UtilityMeterRecord ToRecord() => new(
            MeterId, MeterName, PlantId, EquipmentId, UtilityType, Unit, FdcParameterId,
            ReadingMode, ScaleFactor, CostPerUnit, CarbonPerUnit, IsActive, ConfigVersion);
    }

    private sealed class MeterSaveCommandRow
    {
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string MeterId { get; set; } = "";
        public int ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public string ActorId { get; set; } = "";
        public DateTime CreatedAt { get; set; }

        public UtilityMeterSaveCommandRecord ToRecord() => new(
            IdempotencyKey, RequestHash, MeterId, ExpectedVersion, ResultVersion, ActorId, CreatedAt);
    }

    private sealed class ReadingRow
    {
        public string ReadingId { get; set; } = "";
        public string MeterId { get; set; } = "";
        public string? EquipmentId { get; set; }
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public decimal RawValue { get; set; }
        public decimal NormalizedValue { get; set; }
        public string Unit { get; set; } = "";
        public string Source { get; set; } = "";
        public string SourceEventId { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string Quality { get; set; } = "";
        public DateTime RecordedAt { get; set; }
        public string RecordedBy { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int MeterConfigVersion { get; set; } = 1;
        public string PlantId { get; set; } = "";
        public string ReadingMode { get; set; } = "Cumulative";
        public decimal? CostPerUnit { get; set; }
        public decimal? CarbonPerUnit { get; set; }

        public UtilityReadingRecord ToRecord() => new(
            ReadingId, MeterId, EquipmentId, ProcessLotId, WorkOrderId, RecipeId,
            RecipeVersion, RawValue, NormalizedValue, Unit, Source, SourceEventId,
            RequestHash, Quality, RecordedAt, RecordedBy, CreatedAt,
            MeterConfigVersion, PlantId, ReadingMode, CostPerUnit, CarbonPerUnit);
    }

    private sealed class MeterEventRow
    {
        public string EventId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string MeterId { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string? EquipmentId { get; set; }
        public string EventType { get; set; } = "";
        public DateTime OccurredAt { get; set; }
        public string Reason { get; set; } = "";
        public decimal? PreviousValue { get; set; }
        public decimal? AfterValue { get; set; }
        public decimal? BaselineValue { get; set; }
        public string Unit { get; set; } = "";
        public string ActorUserId { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public int MeterConfigVersion { get; set; } = 1;

        public UtilityMeterEventRecord ToRecord() => new(
            EventId, IdempotencyKey, RequestHash, MeterId, PlantId, EquipmentId,
            EventType, OccurredAt, Reason, PreviousValue, AfterValue, BaselineValue,
            Unit, ActorUserId, CreatedAt, MeterConfigVersion);
    }

    private sealed class MeterConfigHistoryRow
    {
        public string HistoryId { get; set; } = "";
        public string MeterId { get; set; } = "";
        public int ConfigVersion { get; set; }
        public string MeterName { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string? EquipmentId { get; set; }
        public string UtilityType { get; set; } = "";
        public string Unit { get; set; } = "";
        public string? FdcParameterId { get; set; }
        public string ReadingMode { get; set; } = "Cumulative";
        public decimal ScaleFactor { get; set; }
        public decimal? CostPerUnit { get; set; }
        public decimal? CarbonPerUnit { get; set; }
        public bool IsActive { get; set; }
        public string ChangedBy { get; set; } = "";
        public DateTime ChangedAt { get; set; }

        public UtilityMeterConfigHistoryRecord ToRecord() => new(
            HistoryId, MeterId, ConfigVersion, MeterName, PlantId, EquipmentId,
            UtilityType, Unit, FdcParameterId, ReadingMode, ScaleFactor,
            CostPerUnit, CarbonPerUnit, IsActive, ChangedBy, ChangedAt);
    }
}
