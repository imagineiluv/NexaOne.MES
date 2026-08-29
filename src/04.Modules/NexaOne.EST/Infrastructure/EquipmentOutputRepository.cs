using NexaOne.EST.Application.Est;
using NexaOne.Infrastructure.Persistence;
using System.Data.Common;

namespace NexaOne.EST.Infrastructure;

public sealed class EquipmentOutputRepository : QueryRepository, IEquipmentOutputRepository
{
    private readonly ServiceObjectProcessor _processor;

    public EquipmentOutputRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<EquipmentOutputRecord?> GetByIdempotencyKeyAsync(
        string idempotencyKey,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<OutputRow>(
            SelectSql + " WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return row?.ToRecord();
    }

    public async Task<EquipmentOutputRecord?> GetBySourceEventAsync(
        string source,
        string sourceEventId,
        CancellationToken ct = default)
    {
        var row = await QueryFirstOrDefaultAsync<OutputRow>(
            SelectSql + " WHERE SOURCE = @source AND SOURCE_EVENT_ID = @sourceEventId",
            new { source, sourceEventId }, ct);
        return row?.ToRecord();
    }

    public async Task<bool> TryAddAsync(EquipmentOutputRecord r, CancellationToken ct = default)
    {
        var param = new
        {
            r.OutputEventId,
            r.IdempotencyKey,
            r.RequestHash,
            r.PlantId,
            r.EquipmentId,
            r.OutputType,
            r.CarrierId,
            r.ProcessLotId,
            r.WorkOrderId,
            r.ProcessId,
            r.RecipeId,
            r.RecipeVersion,
            r.TotalQuantity,
            r.GoodQuantity,
            r.DefectQuantity,
            r.Unit,
            r.Source,
            r.SourceEventId,
            r.ActorId,
            r.CorrelationId,
            r.MetadataJson,
            r.OccurredAt,
            r.CreatedAt,
            r.IsLotOutput,
            r.WorkScopeId,
        };
        try
        {
            return await _processor.ExecuteGuardedManyAsync(ct, (InsertSql, param));
        }
        catch (DbException)
        {
            if (await GetByIdempotencyKeyAsync(r.IdempotencyKey, ct) is not null)
                return false;
            if (r.SourceEventId is not null
                && await GetBySourceEventAsync(r.Source, r.SourceEventId, ct) is not null)
                return false;
            throw;
        }
    }

    private const string SelectSql = @"
        SELECT OUTPUT_EVENT_ID AS OutputEventId,
               IDEMPOTENCY_KEY AS IdempotencyKey,
               REQUEST_HASH AS RequestHash,
               PLANT_ID AS PlantId,
               EQUIPMENT_ID AS EquipmentId,
               OUTPUT_TYPE AS OutputType,
               CARRIER_ID AS CarrierId,
               PROCESS_LOT_ID AS ProcessLotId,
               WORK_ORDER_ID AS WorkOrderId,
               PROCESS_ID AS ProcessId,
               RECIPE_ID AS RecipeId,
               RECIPE_VERSION AS RecipeVersion,
               TOTAL_QTY AS TotalQuantity,
               GOOD_QTY AS GoodQuantity,
               DEFECT_QTY AS DefectQuantity,
               UNIT AS Unit,
               SOURCE AS Source,
               SOURCE_EVENT_ID AS SourceEventId,
               ACTOR_ID AS ActorId,
               CORRELATION_ID AS CorrelationId,
               METADATA_JSON AS MetadataJson,
               OCCURRED_AT AS OccurredAt,
               CREATED_AT AS CreatedAt,
               IS_LOT_OUTPUT AS IsLotOutput,
               WORK_SCOPE_ID AS WorkScopeId
        FROM EST_EQUIPMENT_OUTPUT_EVENT";

    // INSERT ... SELECT ... WHERE NOT EXISTS is supported by both MSSQL and SQLite and makes the
    // idempotency guard atomic. ExecuteGuardedManyAsync returns false when another caller won.
    private const string InsertSql = @"
        INSERT INTO EST_EQUIPMENT_OUTPUT_EVENT
        (OUTPUT_EVENT_ID, IDEMPOTENCY_KEY, REQUEST_HASH, PLANT_ID, EQUIPMENT_ID, OUTPUT_TYPE,
         CARRIER_ID, PROCESS_LOT_ID, WORK_ORDER_ID, PROCESS_ID, RECIPE_ID, RECIPE_VERSION,
         TOTAL_QTY, GOOD_QTY, DEFECT_QTY, UNIT, SOURCE, SOURCE_EVENT_ID, ACTOR_ID,
         CORRELATION_ID, METADATA_JSON, OCCURRED_AT, CREATED_BY, CREATED_AT, IS_LOT_OUTPUT,
         WORK_SCOPE_ID)
        SELECT @OutputEventId, @IdempotencyKey, @RequestHash, @PlantId, @EquipmentId, @OutputType,
               @CarrierId, @ProcessLotId, @WorkOrderId, @ProcessId, @RecipeId, @RecipeVersion,
               @TotalQuantity, @GoodQuantity, @DefectQuantity, @Unit, @Source, @SourceEventId, @ActorId,
               @CorrelationId, @MetadataJson, @OccurredAt, @ActorId, @CreatedAt, @IsLotOutput,
               @WorkScopeId
        WHERE NOT EXISTS (
            SELECT 1 FROM EST_EQUIPMENT_OUTPUT_EVENT WHERE IDEMPOTENCY_KEY = @IdempotencyKey
        )
          AND (@SourceEventId IS NULL OR NOT EXISTS (
              SELECT 1 FROM EST_EQUIPMENT_OUTPUT_EVENT
              WHERE SOURCE = @Source AND SOURCE_EVENT_ID = @SourceEventId
          ))";

    private sealed class OutputRow
    {
        public string OutputEventId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string RequestHash { get; set; } = "";
        public string PlantId { get; set; } = "";
        public string EquipmentId { get; set; } = "";
        public string OutputType { get; set; } = "";
        public string? CarrierId { get; set; }
        public string? ProcessLotId { get; set; }
        public string? WorkOrderId { get; set; }
        public string? ProcessId { get; set; }
        public string? RecipeId { get; set; }
        public int? RecipeVersion { get; set; }
        public decimal TotalQuantity { get; set; }
        public decimal GoodQuantity { get; set; }
        public decimal DefectQuantity { get; set; }
        public string Unit { get; set; } = "";
        public string Source { get; set; } = "";
        public string? SourceEventId { get; set; }
        public string ActorId { get; set; } = "";
        public string? CorrelationId { get; set; }
        public string? MetadataJson { get; set; }
        public DateTime OccurredAt { get; set; }
        public DateTime CreatedAt { get; set; }
        public bool IsLotOutput { get; set; }
        public string? WorkScopeId { get; set; }

        public EquipmentOutputRecord ToRecord() => new(
            OutputEventId, IdempotencyKey, RequestHash, PlantId, EquipmentId, OutputType,
            CarrierId, ProcessLotId, WorkOrderId, ProcessId, RecipeId, RecipeVersion,
            TotalQuantity, GoodQuantity, DefectQuantity, Unit, Source, SourceEventId,
            ActorId, CorrelationId, MetadataJson, OccurredAt, CreatedAt, IsLotOutput,
            WorkScopeId);
    }
}
