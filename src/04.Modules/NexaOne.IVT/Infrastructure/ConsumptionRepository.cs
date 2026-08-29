using NexaOne.Infrastructure.Persistence;
using NexaOne.IVT.Application.Materials;
using NexaOne.IVT.Domain;
using System.Data.Common;
using System.Globalization;

namespace NexaOne.IVT.Infrastructure;

public sealed class ConsumptionRepository : QueryRepository, IConsumptionRepository
{
    private readonly ServiceObjectProcessor _processor;

    public ConsumptionRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<MaterialLotBalance?> GetLotAsync(
        string materialLotId,
        CancellationToken ct = default)
    {
        const string sql = @"SELECT LOT_ID, MATERIAL_ID, CURRENT_QTY, UNIT, STATUS
                             FROM IVT_MATERIAL_LOT WHERE LOT_ID = @materialLotId";
        var row = await QueryFirstOrDefaultAsync<MaterialLotRow>(sql, new { materialLotId }, ct);
        return row is null
            ? null
            : new MaterialLotBalance(
                row.LotId, row.MaterialId ?? string.Empty, ToDecimal(row.CurrentQty),
                row.Unit ?? string.Empty, row.Status ?? string.Empty);
    }

    public async Task<ConsumptionRecord?> GetByIdAsync(
        string consumptionId,
        CancellationToken ct = default)
    {
        const string sql = ConsumptionSelect + " WHERE H.CONSUMPTION_ID = @consumptionId";
        var row = await QueryFirstOrDefaultAsync<ConsumptionRow>(sql, new { consumptionId }, ct);
        return row?.ToDomain();
    }

    public async Task<ConsumptionRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        const string sql = ConsumptionSelect + " WHERE H.IDEMPOTENCY_KEY = @idempotencyKey";
        var row = await QueryFirstOrDefaultAsync<ConsumptionRow>(sql, new { idempotencyKey }, ct);
        return row?.ToDomain();
    }

    public async Task<ConsumptionRecord?> GetBySourceEventAsync(
        string sourceSystem,
        string sourceEventId,
        CancellationToken ct = default)
    {
        const string sql = ConsumptionSelect + @" WHERE H.SOURCE_SYSTEM = @sourceSystem
                               AND H.SOURCE_EVENT_ID = @sourceEventId
                               AND H.REVERSAL_OF_ID IS NULL";
        var row = await QueryFirstOrDefaultAsync<ConsumptionRow>(
            sql, new { sourceSystem, sourceEventId }, ct);
        return row?.ToDomain();
    }

    public async Task<bool> PersistAsync(ConsumptionRecord record, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var actor = record.OperatorId;
        var txId = Guid.NewGuid().ToString("N");
        var param = ToParam(record, actor, now, txId);

        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (@"UPDATE IVT_MATERIAL_LOT SET
                   CURRENT_QTY = CAST(COALESCE(CURRENT_QTY, 0) AS DECIMAL(38,9))
                                 - CAST(@Quantity AS DECIMAL(38,9)),
                   STATUS = CASE WHEN CAST(COALESCE(CURRENT_QTY, 0) AS DECIMAL(38,9))
                                           - CAST(@Quantity AS DECIMAL(38,9)) = 0
                                 THEN 'Consumed' ELSE STATUS END,
                   VERSION_NO = VERSION_NO + 1,
                   UPDATED_BY = @Actor, UPDATED_AT = @Now
               WHERE LOT_ID = @MaterialLotId
                 AND MATERIAL_ID = @MaterialId
                 AND STATUS = 'InStock'
                 AND CAST(COALESCE(CURRENT_QTY, 0) AS DECIMAL(38,9))
                     >= CAST(@Quantity AS DECIMAL(38,9))
                 AND (@IsTrace = 0 OR EXISTS (
                     SELECT 1 FROM IVT_MATERIAL_FEED_SESSION S
                      WHERE S.FEED_SESSION_ID = @FeedSessionId
                        AND S.MATERIAL_LOT_ID = @MaterialLotId
                        AND S.PLANT_ID = @PlantId
                        AND S.EQUIPMENT_ID = @EquipmentId
                        AND S.STATUS <> 'Cancelled'
                        AND S.MOUNTED_AT <= @OccurredAt
                        AND (S.UNMOUNTED_AT IS NULL OR S.UNMOUNTED_AT > @OccurredAt)))
                 AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_TX T
                                 WHERE T.IDEMPOTENCY_KEY = @IdempotencyKey)
                 AND NOT EXISTS (SELECT 1 FROM IVT_MATERIAL_TX T
                                 WHERE T.SOURCE_SYSTEM = @SourceSystem
                                   AND T.SOURCE_EVENT_ID = @SourceEventId)
                 AND NOT EXISTS (
                     SELECT 1 FROM IVT_MATERIAL_CONSUMPTION_HISTORY H
                     WHERE H.SOURCE_SYSTEM = @SourceSystem
                       AND H.SOURCE_EVENT_ID = @SourceEventId
                       AND H.REVERSAL_OF_ID IS NULL)", param),
                (InsertConsumptionSql, param),
                (InsertTransactionSql, param));
        }
        catch (DbException)
        {
            // A different material-lot transaction can win the global source-event unique race.
            // Translate only that known duplicate to the domain's conflict path; preserve other DB faults.
            if (await GetBySourceEventAsync(record.SourceSystem, record.SourceEventId, ct) is not null
                || await HasTransactionIdentityAsync(
                    record.IdempotencyKey, record.SourceSystem, record.SourceEventId, ct))
                return false;
            throw;
        }
    }

    public async Task<bool> PersistReversalAsync(
        ConsumptionRecord original,
        ConsumptionRecord reversal,
        string reason,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var actor = reversal.OperatorId;
        var txId = Guid.NewGuid().ToString("N");
        var param = ToParam(reversal, actor, now, txId);
        param.Add("OriginalId", original.ConsumptionId);
        param.Add("Reason", reason);

        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct,
                (@"UPDATE IVT_MATERIAL_LOT SET
                       CURRENT_QTY = CAST(COALESCE(CURRENT_QTY, 0) AS DECIMAL(38,9))
                                     + CAST(@Quantity AS DECIMAL(38,9)),
                       STATUS = CASE WHEN STATUS = 'Consumed' THEN 'InStock' ELSE STATUS END,
                       VERSION_NO = VERSION_NO + 1,
                       UPDATED_BY = @Actor, UPDATED_AT = @Now
                   WHERE LOT_ID = @MaterialLotId AND MATERIAL_ID = @MaterialId
                     AND STATUS <> 'Scrapped'
                     AND EXISTS (
                         SELECT 1 FROM IVT_MATERIAL_CONSUMPTION_HISTORY H
                         WHERE H.CONSUMPTION_ID = @OriginalId
                           AND H.REVERSAL_OF_ID IS NULL
                           AND H.MATERIAL_LOT_ID = @MaterialLotId
                           AND H.MATERIAL_ID = @MaterialId)
                     AND NOT EXISTS (
                         SELECT 1 FROM IVT_MATERIAL_CONSUMPTION_HISTORY R
                         WHERE R.REVERSAL_OF_ID = @OriginalId)", param),
                (InsertConsumptionSql, param),
                (InsertReversalTransactionSql, param));
        }
        catch (DbException)
        {
            if (await HasTransactionIdentityAsync(
                    reversal.IdempotencyKey, reversal.SourceSystem, reversal.SourceEventId, ct))
                return false;
            throw;
        }
    }

    private async Task<bool> HasTransactionIdentityAsync(
        string idempotencyKey,
        string sourceSystem,
        string sourceEventId,
        CancellationToken ct)
        => await QueryFirstOrDefaultAsync<string>(
            @"SELECT TX_ID FROM IVT_MATERIAL_TX
              WHERE IDEMPOTENCY_KEY = @idempotencyKey
                 OR (SOURCE_SYSTEM = @sourceSystem AND SOURCE_EVENT_ID = @sourceEventId)",
            new { idempotencyKey, sourceSystem, sourceEventId }, ct) is not null;

    private const string InsertConsumptionSql = @"
        INSERT INTO IVT_MATERIAL_CONSUMPTION_HISTORY
        (CONSUMPTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID,
         MATERIAL_LOT_ID, MATERIAL_ID, PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID,
         RECIPE_ID, RECIPE_VERSION, CONSUMPTION_MODE, QUANTITY, UNIT, TRACE_ID, TAG_ID,
         SOURCE_EVENT_ID, SOURCE_SYSTEM, OPERATOR_ID, FEED_SESSION_ID, CORRELATION_ID,
         WORK_SCOPE_ID, CARRIER_ID, REVERSAL_OF_ID,
         STATUS, METADATA_JSON, OCCURRED_AT, CREATED_BY, CREATED_AT)
        VALUES
        (@ConsumptionId, @IdempotencyKey, @RequestHash, @PlantId, @EquipmentId,
         @MaterialLotId, @MaterialId, @ProcessLotId, @WorkOrderId, @ProcessId,
         @RecipeId, @RecipeVersion, @Mode, @Quantity, @Unit, @TraceId, @TagId,
         @SourceEventId, @SourceSystem, @OperatorId, @FeedSessionId, @CorrelationId,
         @WorkScopeId, @CarrierId, @ReversalOfId,
         @Status, @MetadataJson, @OccurredAt, @Actor, @Now)";

    private const string ConsumptionSelect = @"SELECT H.*,
        CASE WHEN H.REVERSAL_OF_ID IS NULL AND EXISTS (
            SELECT 1 FROM IVT_MATERIAL_CONSUMPTION_HISTORY R
            WHERE R.REVERSAL_OF_ID = H.CONSUMPTION_ID)
        THEN 'Reversed' ELSE H.STATUS END AS EFFECTIVE_STATUS
        FROM IVT_MATERIAL_CONSUMPTION_HISTORY H";

    private const string InsertTransactionSql = @"
        INSERT INTO IVT_MATERIAL_TX
        (TX_ID, LOT_ID, MATERIAL_ID, TX_TYPE, QTY, FROM_WAREHOUSE, TO_WAREHOUSE,
         TX_AT, PROCESSED_BY, STATUS, REMARK, IDEMPOTENCY_KEY, REQUEST_HASH,
         SOURCE_SYSTEM, SOURCE_EVENT_ID, CORRELATION_ID, METADATA_JSON,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
        (@TxId, @MaterialLotId, @MaterialId, 'Consumption', @Quantity, NULL, NULL,
         @OccurredAt, @OperatorId, 'Completed', @ConsumptionRemark, @IdempotencyKey, @RequestHash,
         @SourceSystem, @SourceEventId, @CorrelationId, @MetadataJson,
         @Actor, @Now, @Actor, @Now)";

    private const string InsertReversalTransactionSql = @"
        INSERT INTO IVT_MATERIAL_TX
        (TX_ID, LOT_ID, MATERIAL_ID, TX_TYPE, QTY, FROM_WAREHOUSE, TO_WAREHOUSE,
         TX_AT, PROCESSED_BY, STATUS, REMARK, IDEMPOTENCY_KEY, REQUEST_HASH,
         SOURCE_SYSTEM, SOURCE_EVENT_ID, CORRELATION_ID, METADATA_JSON,
         CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
        VALUES
        (@TxId, @MaterialLotId, @MaterialId, 'Reversal', @Quantity, NULL, NULL,
         @OccurredAt, @OperatorId, 'Completed', @ReversalRemark, @IdempotencyKey, @RequestHash,
         @SourceSystem, @SourceEventId, @CorrelationId, @MetadataJson,
         @Actor, @Now, @Actor, @Now)";

    private static Dapper.DynamicParameters ToParam(
        ConsumptionRecord record,
        string actor,
        DateTime now,
        string txId)
    {
        var p = new Dapper.DynamicParameters();
        p.Add("ConsumptionId", record.ConsumptionId);
        p.Add("IdempotencyKey", record.IdempotencyKey);
        p.Add("RequestHash", record.RequestHash);
        p.Add("PlantId", record.PlantId);
        p.Add("EquipmentId", record.EquipmentId);
        p.Add("MaterialLotId", record.MaterialLotId);
        p.Add("MaterialId", record.MaterialId);
        p.Add("ProcessLotId", record.ProcessLotId);
        p.Add("WorkOrderId", record.WorkOrderId);
        p.Add("ProcessId", record.ProcessId);
        p.Add("RecipeId", record.RecipeId);
        p.Add("RecipeVersion", record.RecipeVersion);
        p.Add("Mode", record.Mode);
        p.Add("IsTrace", string.Equals(record.Mode, "Trace", StringComparison.OrdinalIgnoreCase) ? 1 : 0);
        p.Add("Quantity", record.Quantity);
        p.Add("Unit", record.Unit);
        p.Add("TraceId", record.TraceId);
        p.Add("TagId", record.TagId);
        p.Add("SourceEventId", record.SourceEventId);
        p.Add("SourceSystem", record.SourceSystem);
        p.Add("OperatorId", record.OperatorId);
        p.Add("FeedSessionId", record.FeedSessionId);
        p.Add("CorrelationId", record.CorrelationId);
        p.Add("WorkScopeId", record.WorkScopeId);
        p.Add("CarrierId", record.CarrierId);
        p.Add("ReversalOfId", record.ReversalOfId);
        p.Add("Status", record.Status);
        p.Add("MetadataJson", record.MetadataJson);
        p.Add("OccurredAt", record.OccurredAt);
        p.Add("Actor", actor);
        p.Add("Now", now);
        p.Add("TxId", txId);
        p.Add("ConsumptionRemark", $"Material consumption {record.ConsumptionId}");
        p.Add("ReversalRemark", $"Material consumption reversal {record.ReversalOfId}");
        return p;
    }

    private static decimal ToDecimal(object? value) =>
        value is null or DBNull
            ? 0m
            : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private sealed class MaterialLotRow
    {
        public string LotId { get; set; } = string.Empty;
        public string? MaterialId { get; set; }
        // SQLite materializes DECIMAL affinity as Int64/Double; normalize explicitly.
        public object? CurrentQty { get; set; }
        public string? Unit { get; set; }
        public string? Status { get; set; }
    }

    private sealed class ConsumptionRow
    {
        public string ConsumptionId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string PlantId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public string MaterialLotId { get; set; } = string.Empty;
        public string MaterialId { get; set; } = string.Empty;
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public string ConsumptionMode { get; set; } = string.Empty;
        public object? Quantity { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string? TraceId { get; set; }
        public string? TagId { get; set; }
        public string SourceEventId { get; set; } = string.Empty;
        public string SourceSystem { get; set; } = string.Empty;
        public string OperatorId { get; set; } = string.Empty;
        public string? FeedSessionId { get; set; }
        public string? CorrelationId { get; set; }
        public string? WorkScopeId { get; set; }
        public string? CarrierId { get; set; }
        public string? ReversalOfId { get; set; }
        public string Status { get; set; } = string.Empty;
        public string EffectiveStatus { get; set; } = string.Empty;
        public string? MetadataJson { get; set; }
        public DateTime OccurredAt { get; set; }

        public ConsumptionRecord ToDomain() => new(
            ConsumptionId, IdempotencyKey, RequestHash, PlantId, EquipmentId,
            MaterialLotId, MaterialId, ProcessLotId, WorkOrderId, ProcessId, RecipeId,
            RecipeVersion, ConsumptionMode, ToDecimal(Quantity), Unit, TraceId, TagId, SourceEventId,
            SourceSystem, OperatorId, FeedSessionId, CorrelationId, ReversalOfId,
            string.IsNullOrEmpty(EffectiveStatus) ? Status : EffectiveStatus,
            MetadataJson, OccurredAt, WorkScopeId, CarrierId);
    }
}
