using NexaOne.EST.Application.Est;
using NexaOne.Infrastructure.Persistence;
using NexusCom.Data.Abstractions.Interfaces;
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

    public Task SaveMeterAsync(UtilityMeterRecord m, string actorId, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var param = new
        {
            m.MeterId, m.MeterName, m.PlantId, m.EquipmentId, m.UtilityType, m.Unit,
            m.FdcParameterId, m.ReadingMode, m.ScaleFactor, m.CostPerUnit, m.CarbonPerUnit,
            m.IsActive, ActorId = actorId, Now = now,
        };
        return _processor.ExecuteManyAsync(ct, (UpdateMeterSql, param), (InsertMeterSql, param));
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
            }));
        }
        catch (DbException)
        {
            if (await GetReadingAsync(r.Source, r.SourceEventId, ct) is not null) return false;
            throw;
        }
    }

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
               CARBON_PER_UNIT AS CarbonPerUnit, IS_ACTIVE AS IsActive
        FROM EST_UTILITY_METER";

    private const string ReadingSelect = @"
        SELECT READING_ID AS ReadingId, METER_ID AS MeterId, EQUIPMENT_ID AS EquipmentId,
               PROCESS_LOT_ID AS ProcessLotId, WORK_ORDER_ID AS WorkOrderId, RECIPE_ID AS RecipeId,
               RECIPE_VERSION AS RecipeVersion, RAW_VALUE AS RawValue,
               NORMALIZED_VALUE AS NormalizedValue, UNIT AS Unit, SOURCE AS Source,
               SOURCE_EVENT_ID AS SourceEventId, REQUEST_HASH AS RequestHash, QUALITY AS Quality,
               RECORDED_AT AS RecordedAt, RECORDED_BY AS RecordedBy, CREATED_AT AS CreatedAt
        FROM EST_UTILITY_READING";

    private const string UpdateMeterSql = @"
        UPDATE EST_UTILITY_METER SET
            METER_NAME=@MeterName, PLANT_ID=@PlantId, EQUIPMENT_ID=@EquipmentId,
            UTILITY_TYPE=@UtilityType, UNIT=@Unit, FDC_PARAMETER_ID=@FdcParameterId,
            READING_MODE=@ReadingMode, SCALE_FACTOR=@ScaleFactor, COST_PER_UNIT=@CostPerUnit,
            CARBON_PER_UNIT=@CarbonPerUnit, IS_ACTIVE=@IsActive,
            UPDATED_BY=@ActorId, UPDATED_AT=@Now
        WHERE METER_ID=@MeterId";

    private const string InsertMeterSql = @"
        INSERT INTO EST_UTILITY_METER
        (METER_ID, METER_NAME, PLANT_ID, EQUIPMENT_ID, UTILITY_TYPE, UNIT, FDC_PARAMETER_ID,
         READING_MODE, SCALE_FACTOR, COST_PER_UNIT, CARBON_PER_UNIT, IS_ACTIVE,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        SELECT @MeterId, @MeterName, @PlantId, @EquipmentId, @UtilityType, @Unit, @FdcParameterId,
               @ReadingMode, @ScaleFactor, @CostPerUnit, @CarbonPerUnit, @IsActive,
               @ActorId, @Now, @ActorId, @Now
        WHERE NOT EXISTS (SELECT 1 FROM EST_UTILITY_METER WHERE METER_ID=@MeterId)";

    private const string InsertReadingSql = @"
        INSERT INTO EST_UTILITY_READING
        (READING_ID, METER_ID, EQUIPMENT_ID, PROCESS_LOT_ID, WORK_ORDER_ID, RECIPE_ID,
         RECIPE_VERSION, RAW_VALUE, NORMALIZED_VALUE, UNIT, SOURCE, SOURCE_EVENT_ID,
         REQUEST_HASH, QUALITY, RECORDED_AT, RECORDED_BY, CREATED_AT)
        SELECT @ReadingId, @MeterId, @EquipmentId, @ProcessLotId, @WorkOrderId, @RecipeId,
               @RecipeVersion, @RawValue, @NormalizedValue, @Unit, @Source, @SourceEventId,
               @RequestHash, @Quality, @RecordedAt, @RecordedBy, @CreatedAt
        WHERE NOT EXISTS (
            SELECT 1 FROM EST_UTILITY_READING
            WHERE SOURCE=@Source AND SOURCE_EVENT_ID=@SourceEventId
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

        public UtilityMeterRecord ToRecord() => new(
            MeterId, MeterName, PlantId, EquipmentId, UtilityType, Unit, FdcParameterId,
            ReadingMode, ScaleFactor, CostPerUnit, CarbonPerUnit, IsActive);
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

        public UtilityReadingRecord ToRecord() => new(
            ReadingId, MeterId, EquipmentId, ProcessLotId, WorkOrderId, RecipeId,
            RecipeVersion, RawValue, NormalizedValue, Unit, Source, SourceEventId,
            RequestHash, Quality, RecordedAt, RecordedBy, CreatedAt);
    }
}
