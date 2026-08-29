using System.Data.Common;
using System.Globalization;
using Dapper;
using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using NexaDB.Data.Abstractions.Models;

namespace NexaOne.IVT.Infrastructure;

internal sealed class TraceBindingRepository : QueryRepository, ITraceBindingRepository
{
    private readonly ServiceObjectProcessor _processor;

    public TraceBindingRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<TraceBindingState?> GetAsync(
        string bindingId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT BINDING_ID AS BindingId, PLANT_ID AS PlantId, EQUIPMENT_ID AS EquipmentId,
                   PARAMETER_ID AS ParameterId, FEED_POINT_ID AS FeedPointId,
                   CALCULATION_MODE AS CalculationMode, SCALE_FACTOR AS ScaleFactor,
                   PULSE_QUANTITY AS PulseQuantity, OUTPUT_UNIT AS OutputUnit,
                   EFFECTIVE_FROM AS EffectiveFrom, EFFECTIVE_TO AS EffectiveTo,
                   IS_ACTIVE AS IsActive, VERSION_NO AS VersionNo,
                   CREATED_BY AS CreatedBy, CREATED_AT AS CreatedAt,
                   UPDATED_BY AS UpdatedBy, UPDATED_AT AS UpdatedAt
              FROM IVT_TRACE_CONSUMPTION_BINDING
             WHERE BINDING_ID = @bindingId
            """;
        var row = await QueryFirstOrDefaultAsync<BindingRow>(sql, new { bindingId }, ct);
        return row?.ToDomain();
    }

    public async Task<TraceBindingCursor?> GetIngestionCursorAsync(
        string bindingId,
        CancellationToken ct = default)
    {
        const string sql = """
            SELECT LAST_COLLECT_ID AS LastCollectId,
                   LAST_COLLECTED_AT AS LastCollectedAt
              FROM IVT_TRACE_INGESTION_CURSOR
             WHERE BINDING_ID = @bindingId
            """;
        var row = await QueryFirstOrDefaultAsync<CursorRow>(sql, new { bindingId }, ct);
        return row is null
            ? null
            : new TraceBindingCursor(row.LastCollectId, Utc(row.LastCollectedAt));
    }

    public Task<TraceBindingWrite?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default) =>
        GetCommandAsync("IDEMPOTENCY_KEY = @idempotencyKey", new { idempotencyKey }, ct);

    public Task<TraceBindingWrite?> GetBySourceEventAsync(
        string sourceSystem,
        string sourceEventId,
        CancellationToken ct = default) =>
        GetCommandAsync(
            "SOURCE_SYSTEM = @sourceSystem AND SOURCE_EVENT_ID = @sourceEventId",
            new { sourceSystem, sourceEventId },
            ct);

    public async Task<bool> TryCreateAsync(
        TraceBindingState binding,
        TraceBindingWrite write,
        CancellationToken ct = default)
    {
        var parameters = Parameters(write);
        const string insertBinding = """
            INSERT INTO IVT_TRACE_CONSUMPTION_BINDING
              (BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
               CALCULATION_MODE, SCALE_FACTOR, PULSE_QUANTITY, OUTPUT_UNIT,
               EFFECTIVE_FROM, EFFECTIVE_TO, IS_ACTIVE, VERSION_NO,
               CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            SELECT @BindingId, @PlantId, @EquipmentId, @ParameterId, @FeedPointId,
                   @CalculationMode, @ScaleFactor, @PulseQuantity, @OutputUnit,
                   @EffectiveFrom, NULL, 1, 1,
                   @ActorId, @CommittedAt, @ActorId, @CommittedAt
             WHERE NOT EXISTS (
                       SELECT 1 FROM IVT_TRACE_CONSUMPTION_BINDING WHERE BINDING_ID = @BindingId)
                AND NOT EXISTS (
                        SELECT 1 FROM IVT_TRACE_CONSUMPTION_BINDING
                         WHERE EQUIPMENT_ID = @EquipmentId
                           AND PARAMETER_ID = @ParameterId
                           AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO > @EffectiveFrom))
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_TRACE_BINDING_COMMAND WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_TRACE_BINDING_COMMAND
                        WHERE SOURCE_SYSTEM = @SourceSystem AND SOURCE_EVENT_ID = @SourceEventId)
            """;
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct,
                (insertBinding, parameters),
                (InsertCommandSql, parameters));
        }
        catch (DbException)
        {
            if (await HasKnownConflictAsync(write, includeBindingIdentity: true, ct)) return false;
            throw;
        }
    }

    public async Task<bool> TryRetireAsync(
        TraceBindingState binding,
        int expectedVersion,
        TraceBindingWrite write,
        CancellationToken ct = default)
    {
        var parameters = Parameters(write);
        const string updateBinding = """
            UPDATE IVT_TRACE_CONSUMPTION_BINDING SET
                   EFFECTIVE_TO = @EffectiveTo,
                   IS_ACTIVE = 0,
                   VERSION_NO = @ResultVersion,
                   UPDATED_BY = @ActorId,
                   UPDATED_AT = @CommittedAt
             WHERE BINDING_ID = @BindingId
               AND IS_ACTIVE = 1
               AND VERSION_NO = @ExpectedVersion
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_TRACE_BINDING_COMMAND WHERE IDEMPOTENCY_KEY = @IdempotencyKey)
               AND NOT EXISTS (
                       SELECT 1 FROM IVT_TRACE_BINDING_COMMAND
                        WHERE SOURCE_SYSTEM = @SourceSystem AND SOURCE_EVENT_ID = @SourceEventId)
            """;
        try
        {
            return await _processor.ExecuteGuardedManyAsync(
                ct,
                (updateBinding, parameters),
                (InsertCommandSql, parameters));
        }
        catch (DbException)
        {
            if (await HasKnownConflictAsync(write, includeBindingIdentity: false, ct)) return false;
            throw;
        }
    }

    private async Task<bool> HasKnownConflictAsync(
        TraceBindingWrite write,
        bool includeBindingIdentity,
        CancellationToken ct)
    {
        if (await GetByIdempotencyKeyAsync(write.IdempotencyKey, ct) is not null
            || await GetBySourceEventAsync(write.SourceSystem, write.SourceEventId, ct) is not null)
        {
            return true;
        }
        if (includeBindingIdentity && await GetAsync(write.Result.BindingId, ct) is not null) return true;
        if (!includeBindingIdentity) return false;

        const string sql = """
            SELECT COUNT(*)
              FROM IVT_TRACE_CONSUMPTION_BINDING
             WHERE EQUIPMENT_ID = @EquipmentId
               AND PARAMETER_ID = @ParameterId
               AND (EFFECTIVE_TO IS NULL OR EFFECTIVE_TO > @EffectiveFrom)
            """;
        return await CountAsync(sql, write.Result, ct) > 0;
    }

    private async Task<TraceBindingWrite?> GetCommandAsync(
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
               BINDING_ID AS BindingId, PLANT_ID AS PlantId, EQUIPMENT_ID AS EquipmentId,
               PARAMETER_ID AS ParameterId, FEED_POINT_ID AS FeedPointId,
               CALCULATION_MODE AS CalculationMode, SCALE_FACTOR AS ScaleFactor,
               PULSE_QUANTITY AS PulseQuantity, OUTPUT_UNIT AS OutputUnit,
               EFFECTIVE_FROM AS EffectiveFrom, EFFECTIVE_TO AS EffectiveTo,
               RESULT_IS_ACTIVE AS ResultIsActive, EXPECTED_VERSION AS ExpectedVersion,
               RESULT_VERSION AS ResultVersion, ACTOR_ID AS ActorId,
               OCCURRED_AT AS OccurredAt, SOURCE_SYSTEM AS SourceSystem,
               SOURCE_EVENT_ID AS SourceEventId, CORRELATION_ID AS CorrelationId,
               REASON AS Reason, CREATED_AT AS CreatedAt
          FROM IVT_TRACE_BINDING_COMMAND
        """;

    private const string InsertCommandSql = """
        INSERT INTO IVT_TRACE_BINDING_COMMAND
          (COMMAND_ID, COMMAND_TYPE, IDEMPOTENCY_KEY, REQUEST_HASH,
           BINDING_ID, PLANT_ID, EQUIPMENT_ID, PARAMETER_ID, FEED_POINT_ID,
           CALCULATION_MODE, SCALE_FACTOR, PULSE_QUANTITY, OUTPUT_UNIT,
           EFFECTIVE_FROM, EFFECTIVE_TO, RESULT_IS_ACTIVE,
           EXPECTED_VERSION, RESULT_VERSION, ACTOR_ID, OCCURRED_AT,
           SOURCE_SYSTEM, SOURCE_EVENT_ID, CORRELATION_ID, REASON,
           CREATED_BY, CREATED_AT)
        VALUES
          (@CommandId, @Operation, @IdempotencyKey, @RequestHash,
           @BindingId, @PlantId, @EquipmentId, @ParameterId, @FeedPointId,
           @CalculationMode, @ScaleFactor, @PulseQuantity, @OutputUnit,
           @EffectiveFrom, @EffectiveTo, @ResultIsActive,
           @ExpectedVersion, @ResultVersion, @ActorId, @OccurredAt,
           @SourceSystem, @SourceEventId, @CorrelationId, @Reason,
           @ActorId, @CommittedAt)
        """;

    private static DynamicParameters Parameters(TraceBindingWrite write)
    {
        var state = write.Result;
        var parameters = new DynamicParameters();
        parameters.Add("CommandId", write.CommandId);
        parameters.Add("Operation", write.Operation);
        parameters.Add("IdempotencyKey", write.IdempotencyKey);
        parameters.Add("RequestHash", write.RequestHash);
        parameters.Add("BindingId", state.BindingId);
        parameters.Add("PlantId", state.PlantId);
        parameters.Add("EquipmentId", state.EquipmentId);
        parameters.Add("ParameterId", state.ParameterId);
        parameters.Add("FeedPointId", state.FeedPointId);
        parameters.Add("CalculationMode", state.CalculationMode);
        parameters.Add("ScaleFactor", state.ScaleFactor);
        parameters.Add("PulseQuantity", state.PulseQuantity);
        parameters.Add("OutputUnit", state.OutputUnit);
        parameters.Add("EffectiveFrom", state.EffectiveFrom);
        parameters.Add("EffectiveTo", state.EffectiveTo);
        parameters.Add("ResultIsActive", state.IsActive);
        parameters.Add("ExpectedVersion", write.ExpectedVersion);
        parameters.Add("ResultVersion", state.Version);
        parameters.Add("ActorId", write.ActorId);
        parameters.Add("OccurredAt", write.OccurredAt);
        parameters.Add("SourceSystem", write.SourceSystem);
        parameters.Add("SourceEventId", write.SourceEventId);
        parameters.Add("CorrelationId", write.CorrelationId);
        parameters.Add("Reason", write.Reason);
        parameters.Add("CommittedAt", DateTime.UtcNow);
        return parameters;
    }

    private static decimal Decimal(object? value) => value is null or DBNull
        ? 0m
        : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static DateTime Utc(DateTime value) => value.Kind == DateTimeKind.Utc
        ? value
        : DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is { } timestamp
        ? Utc(timestamp)
        : null;

    private sealed class BindingRow
    {
        public string BindingId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string ParameterId { get; set; } = string.Empty;
        public string FeedPointId { get; set; } = string.Empty;
        public string CalculationMode { get; set; } = string.Empty;
        public object? ScaleFactor { get; set; }
        public object? PulseQuantity { get; set; }
        public string OutputUnit { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public bool IsActive { get; set; }
        public int VersionNo { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string UpdatedBy { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }

        public TraceBindingState ToDomain() => new(
            BindingId, PlantId, EquipmentId, ParameterId, FeedPointId, CalculationMode,
            Decimal(ScaleFactor), PulseQuantity is null or DBNull ? null : Decimal(PulseQuantity),
            OutputUnit, Utc(EffectiveFrom), Utc(EffectiveTo), IsActive, VersionNo,
            CreatedBy, Utc(CreatedAt), UpdatedBy, Utc(UpdatedAt));
    }

    private sealed class CommandRow
    {
        public string CommandId { get; set; } = string.Empty;
        public string CommandType { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string BindingId { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string ParameterId { get; set; } = string.Empty;
        public string FeedPointId { get; set; } = string.Empty;
        public string CalculationMode { get; set; } = string.Empty;
        public object? ScaleFactor { get; set; }
        public object? PulseQuantity { get; set; }
        public string OutputUnit { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public bool ResultIsActive { get; set; }
        public int ExpectedVersion { get; set; }
        public int ResultVersion { get; set; }
        public string ActorId { get; set; } = string.Empty;
        public DateTime OccurredAt { get; set; }
        public string SourceSystem { get; set; } = string.Empty;
        public string SourceEventId { get; set; } = string.Empty;
        public string? CorrelationId { get; set; }
        public string? Reason { get; set; }
        public DateTime CreatedAt { get; set; }

        public TraceBindingWrite ToDomain()
        {
            var state = new TraceBindingState(
                BindingId, PlantId, EquipmentId, ParameterId, FeedPointId, CalculationMode,
                Decimal(ScaleFactor), PulseQuantity is null or DBNull ? null : Decimal(PulseQuantity),
                OutputUnit, Utc(EffectiveFrom), Utc(EffectiveTo), ResultIsActive, ResultVersion,
                ActorId, Utc(CreatedAt), ActorId, Utc(CreatedAt));
            return new TraceBindingWrite(
                CommandId, CommandType, IdempotencyKey, RequestHash, state,
                ExpectedVersion, ActorId, Utc(OccurredAt), SourceSystem, SourceEventId,
                CorrelationId, Reason);
        }
    }

    private sealed class CursorRow
    {
        public string LastCollectId { get; set; } = string.Empty;
        public DateTime LastCollectedAt { get; set; }
    }
}
