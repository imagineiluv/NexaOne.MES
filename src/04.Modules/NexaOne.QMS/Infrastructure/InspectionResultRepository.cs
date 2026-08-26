using System.Text.Json;
using System.Text.Json.Serialization;
using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

public sealed class InspectionResultRepository : QueryRepository, IInspectionResultRepository
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ServiceObjectProcessor _processor;

    public InspectionResultRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    public async Task<IReadOnlyList<InspectionResult>> GetByLotAsync(
        string lotId, CancellationToken ct = default)
    {
        const string sql = @"SELECT R.*, I.INSPECTION_TYPE
            FROM QMS_INSPECTION_RESULT R
            LEFT JOIN QMS_INSPECTION I ON I.INSPECTION_ID = R.INSPECTION_ID
            WHERE R.LOT_ID = @lotId
            ORDER BY R.INSPECTED_AT DESC, R.ITEM_SEQUENCE";
        var rows = await QueryAsync<ResultRow>(sql, new { lotId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    public async Task<IReadOnlyList<InspectionResult>> GetBySpecAsync(
        string specId, CancellationToken ct = default)
    {
        const string sql = @"SELECT R.*, I.INSPECTION_TYPE
            FROM QMS_INSPECTION_RESULT R
            LEFT JOIN QMS_INSPECTION I ON I.INSPECTION_ID = R.INSPECTION_ID
            WHERE R.SPEC_ID = @specId
            ORDER BY R.INSPECTED_AT DESC, R.ITEM_SEQUENCE";
        var rows = await QueryAsync<ResultRow>(sql, new { specId }, ct);
        return rows.Select(r => r.ToDomain()).ToList();
    }

    /// <summary>기존 단건 API의 헤더와 결과를 같은 트랜잭션으로 저장합니다.</summary>
    public async Task AddAsync(InspectionResult result, CancellationToken ct = default)
    {
        // The legacy single-result path intentionally leaves ITEM_SEQUENCE null. A non-null
        // sequence is the persisted v2 discriminator and therefore requires a v2/idempotent header.
        const string resultSql = @"INSERT INTO QMS_INSPECTION_RESULT
            (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID, MEASURED_VALUE, ATTRIBUTE_RESULT,
             INSPECTED_AT, INSPECTOR_ID, IS_PASS, REMARK, ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@ResultId, @InspectionId, @SpecId, @LotId, @EquipmentId, @MeasuredValue, @AttributeResult,
             @InspectedAt, @InspectorId, @IsPass, @Remark, NULL, @SampleQuantity, @DefectQuantity,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";
        const string inspectionSql = @"INSERT INTO QMS_INSPECTION
            (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, PRODUCT_ID, EQUIPMENT_ID, SPEC_ID,
             INSPECTED_AT, INSPECTOR_ID, RESULT, SAMPLE_QTY, DEFECT_QTY, IS_CONFIRMED, REMARK,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@InspectionId, @InspectionType, @LotId,
             COALESCE((SELECT PRODUCT_ID FROM POM_LOT WHERE LOT_ID = @LotId),
                      (SELECT MATERIAL_ID FROM IVT_MATERIAL_LOT WHERE LOT_ID = @LotId)),
             @EquipmentId, @SpecId,
             @InspectedAt, @InspectorId, @Verdict, @SampleQuantity, @DefectQuantity, 1, @Remark,
             @CreatedBy, @CreatedAt, @UpdatedBy, @UpdatedAt)";

        var now = DateTime.UtcNow;
        var row = new
        {
            ResultId = result.Id,
            result.InspectionId,
            result.SpecId,
            result.LotId,
            result.EquipmentId,
            result.MeasuredValue,
            result.AttributeResult,
            result.InspectedAt,
            result.InspectorId,
            result.IsPass,
            result.Remark,
            result.SampleQuantity,
            result.DefectQuantity,
            CreatedBy = result.InspectorId,
            CreatedAt = now,
            UpdatedBy = result.InspectorId,
            UpdatedAt = now
        };
        var header = new
        {
            result.InspectionId,
            InspectionType = result.InspectionType.ToString(),
            result.LotId,
            result.EquipmentId,
            result.SpecId,
            result.InspectedAt,
            result.InspectorId,
            Verdict = result.IsPass ? "Pass" : "Fail",
            result.SampleQuantity,
            result.DefectQuantity,
            result.Remark,
            CreatedBy = result.InspectorId,
            CreatedAt = now,
            UpdatedBy = result.InspectorId,
            UpdatedAt = now
        };
        await _processor.ExecuteManyAsync(ct, (inspectionSql, header), (resultSql, row));
    }

    public async Task<InspectionExecution?> GetExecutionAsync(
        string inspectionId, CancellationToken ct = default)
    {
        var header = await QueryFirstOrDefaultAsync<ExecutionRow>(@"SELECT *
            FROM QMS_INSPECTION
            WHERE INSPECTION_ID = @inspectionId AND IDEMPOTENCY_KEY IS NOT NULL",
            new { inspectionId }, ct);
        if (header is null) return null;

        var items = await QueryAsync<ResultRow>(@"SELECT R.*, I.INSPECTION_TYPE
            FROM QMS_INSPECTION_RESULT R
            JOIN QMS_INSPECTION I ON I.INSPECTION_ID = R.INSPECTION_ID
            WHERE R.INSPECTION_ID = @inspectionId
            ORDER BY R.ITEM_SEQUENCE, R.RESULT_ID", new { inspectionId }, ct);
        var history = await QueryAsync<HistoryRow>(@"SELECT *
            FROM QMS_INSPECTION_EVENT
            WHERE INSPECTION_ID = @inspectionId
            ORDER BY OCCURRED_AT, EVENT_ID", new { inspectionId }, ct);

        return header.ToDomain(
            items.Select(x => x.ToDomain()).ToArray(),
            history.Select(x => x.ToDomain()).ToArray());
    }

    public async Task<InspectionExecution?> GetExecutionByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
    {
        var id = await QueryFirstOrDefaultAsync<InspectionIdRow>(@"SELECT INSPECTION_ID
            FROM QMS_INSPECTION WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct);
        return id is null ? null : await GetExecutionAsync(id.InspectionId, ct);
    }

    public async Task<SamplingPlanRevision?> GetSamplingPlanRevisionAsync(
        string planRevisionId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<SamplingRow>(@"SELECT *
            FROM QMS_SAMPLING_PLAN_REVISION WHERE PLAN_REVISION_ID = @planRevisionId",
            new { planRevisionId }, ct))?.ToDomain();

    public async Task AddExecutionAsync(
        InspectionExecution execution,
        InspectionExecutionHistory confirmation,
        InspectionExecutionHistory? parentRelation,
        CancellationToken ct = default)
    {
        const string headerSql = @"INSERT INTO QMS_INSPECTION
            (INSPECTION_ID, INSPECTION_TYPE, LOT_ID, PRODUCT_ID, EQUIPMENT_ID, SPEC_ID,
             INSPECTED_AT, INSPECTOR_ID, RESULT, LOT_QTY, SAMPLE_QTY, DEFECT_QTY,
             IS_CONFIRMED, REMARK, IDEMPOTENCY_KEY, REQUEST_HASH,
             SAMPLING_PLAN_REVISION_ID, SAMPLING_SNAPSHOT_JSON,
             RELATION_TYPE, PARENT_INSPECTION_ID, ROOT_INSPECTION_ID,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@InspectionId, @InspectionType, @LotId,
             COALESCE((SELECT PRODUCT_ID FROM POM_LOT WHERE LOT_ID = @LotId),
                      (SELECT MATERIAL_ID FROM IVT_MATERIAL_LOT WHERE LOT_ID = @LotId)),
             @EquipmentId, NULL, @InspectedAt, @InspectorId, @Result,
             @LotQuantity, @SampleQuantity, @DefectQuantity, 1, @Remark,
             @IdempotencyKey, @RequestHash, @SamplingPlanRevisionId, @SamplingSnapshotJson,
             @RelationType, @ParentInspectionId, @RootInspectionId,
             @InspectorId, @InspectedAt, @InspectorId, @InspectedAt)";
        const string resultSql = @"INSERT INTO QMS_INSPECTION_RESULT
            (RESULT_ID, INSPECTION_ID, SPEC_ID, LOT_ID, EQUIPMENT_ID,
             MEASURED_VALUE, ATTRIBUTE_RESULT, INSPECTED_AT, INSPECTOR_ID,
             IS_PASS, REMARK, ITEM_SEQUENCE, SAMPLE_QTY, DEFECT_QTY,
             CREATED_BY, CREATED_AT, UPDATED_BY, UPDATED_AT)
            VALUES
            (@ResultId, @InspectionId, @SpecId, @LotId, @EquipmentId,
             @MeasuredValue, @AttributeResult, @InspectedAt, @InspectorId,
             @IsPass, @Remark, @ItemSequence, @SampleQuantity, @DefectQuantity,
             @InspectorId, @InspectedAt, @InspectorId, @InspectedAt)";

        var statements = new List<(string Sql, object? Param)>
        {
            (headerSql, new
            {
                execution.InspectionId,
                InspectionType = execution.InspectionType.ToString(),
                execution.LotId,
                execution.EquipmentId,
                execution.InspectedAt,
                execution.InspectorId,
                Result = execution.IsPass ? "Pass" : "Fail",
                execution.LotQuantity,
                execution.SampleQuantity,
                execution.DefectQuantity,
                execution.Remark,
                execution.IdempotencyKey,
                execution.RequestHash,
                SamplingPlanRevisionId = execution.SamplingPlan?.PlanRevisionId,
                SamplingSnapshotJson = execution.SamplingPlan is null
                    ? null
                    : JsonSerializer.Serialize(execution.SamplingPlan, SnapshotJsonOptions),
                RelationType = execution.RelationType.ToString(),
                execution.ParentInspectionId,
                execution.RootInspectionId
            })
        };

        for (var index = 0; index < execution.Items.Count; index++)
        {
            var item = execution.Items[index];
            statements.Add((resultSql, new
            {
                ResultId = item.Id,
                execution.InspectionId,
                item.SpecId,
                execution.LotId,
                execution.EquipmentId,
                item.MeasuredValue,
                item.AttributeResult,
                execution.InspectedAt,
                execution.InspectorId,
                item.IsPass,
                item.Remark,
                ItemSequence = index + 1,
                item.SampleQuantity,
                item.DefectQuantity
            }));
        }

        statements.Add(HistoryStatement(confirmation));
        if (parentRelation is not null) statements.Add(HistoryStatement(parentRelation));
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
    }

    public async Task<InspectionExecutionHistory?> GetHistoryByIdempotencyKeyAsync(
        string inspectionId, string idempotencyKey, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<HistoryRow>(@"SELECT *
            FROM QMS_INSPECTION_EVENT
            WHERE INSPECTION_ID = @inspectionId AND IDEMPOTENCY_KEY = @idempotencyKey",
            new { inspectionId, idempotencyKey }, ct))?.ToDomain();

    public async Task<InspectionExecutionHistory?> GetCancellationHistoryAsync(
        string inspectionId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<HistoryRow>(@"SELECT *
            FROM QMS_INSPECTION_EVENT
            WHERE INSPECTION_ID = @inspectionId AND EVENT_TYPE = 'Cancelled'",
            new { inspectionId }, ct))?.ToDomain();

    public async Task<EffectiveLotInspectionStatus> GetEffectiveLotStatusAsync(
        string lotId, CancellationToken ct = default)
    {
        var rows = await QueryAsync<EffectiveStatusRow>(@"SELECT
                R.RESULT_ID, R.INSPECTION_ID, R.IS_PASS, R.INSPECTED_AT,
                I.IDEMPOTENCY_KEY,
                CASE WHEN EXISTS (
                    SELECT 1 FROM QMS_INSPECTION_EVENT E
                    WHERE E.INSPECTION_ID = I.INSPECTION_ID
                      AND E.EVENT_TYPE = 'Cancelled') THEN 1 ELSE 0 END AS IS_CANCELLED,
                CASE WHEN EXISTS (
                    SELECT 1 FROM QMS_INSPECTION_EVENT E
                    WHERE E.INSPECTION_ID = I.INSPECTION_ID
                      AND E.EVENT_TYPE IN ('Corrected', 'Reinspected')) THEN 1 ELSE 0 END AS HAS_SUCCESSOR
            FROM QMS_INSPECTION_RESULT R
            LEFT JOIN QMS_INSPECTION I ON I.INSPECTION_ID = R.INSPECTION_ID
            WHERE R.LOT_ID = @lotId",
            new { lotId }, ct);

        var v2Rows = rows.Where(x => !string.IsNullOrWhiteSpace(x.IdempotencyKey)).ToArray();
        var effectiveV2 = v2Rows
            .Where(x => x.HasSuccessor == 0 && x.IsCancelled == 0)
            .GroupBy(x => x.InspectionId, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Max(x => x.InspectedAt))
            .ThenByDescending(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        if (effectiveV2 is not null)
        {
            var executionRows = effectiveV2.ToArray();
            var failed = executionRows.Count(x => !x.IsPass);
            return new EffectiveLotInspectionStatus(
                true,
                failed == 0,
                executionRows.Length,
                failed,
                executionRows.Max(x => x.InspectedAt));
        }


        // Once a lot has v2 evidence, cancelled/superseded rows must not make the lot look
        // failed or fall back to stale legacy evidence. No effective leaf means Pending.
        if (v2Rows.Length > 0)
            return new EffectiveLotInspectionStatus(false, false, 0, 0, null);

        var legacyRows = rows.Where(x => string.IsNullOrWhiteSpace(x.IdempotencyKey)).ToArray();
        var legacyFailed = legacyRows.Count(x => !x.IsPass);
        return new EffectiveLotInspectionStatus(
            legacyRows.Length > 0,
            legacyRows.Length > 0 && legacyFailed == 0,
            legacyRows.Length,
            legacyFailed,
            legacyRows.Length == 0 ? null : legacyRows.Max(x => x.InspectedAt));
    }

    public Task AppendHistoryAsync(
        InspectionExecutionHistory history, CancellationToken ct = default)
    {
        var statement = HistoryStatement(history);
        return _processor.ExecuteAsync(statement.Sql, statement.Param, ct);
    }

    private static (string Sql, object? Param) HistoryStatement(InspectionExecutionHistory history)
    {
        const string sql = @"INSERT INTO QMS_INSPECTION_EVENT
            (EVENT_ID, INSPECTION_ID, EVENT_TYPE, RELATED_INSPECTION_ID,
             PARENT_INSPECTION_ID, ROOT_INSPECTION_ID, IDEMPOTENCY_KEY, REQUEST_HASH,
             ACTOR_ID, REASON, OCCURRED_AT, CREATED_BY, CREATED_AT)
            VALUES
            (@EventId, @InspectionId, @EventType, @RelatedInspectionId,
             @ParentInspectionId, @RootInspectionId, @IdempotencyKey, @RequestHash,
             @ActorId, @Reason, @OccurredAt, @ActorId, @OccurredAt)";
        return (sql, new
        {
            history.EventId,
            history.InspectionId,
            EventType = history.EventType.ToString(),
            history.RelatedInspectionId,
            history.ParentInspectionId,
            history.RootInspectionId,
            history.IdempotencyKey,
            history.RequestHash,
            history.ActorId,
            history.Reason,
            history.OccurredAt
        });
    }

    private sealed class InspectionIdRow
    {
        public string InspectionId { get; set; } = string.Empty;
    }

    private sealed class EffectiveStatusRow
    {
        public string ResultId { get; set; } = string.Empty;
        public string InspectionId { get; set; } = string.Empty;
        public bool IsPass { get; set; }
        public DateTime InspectedAt { get; set; }
        public string? IdempotencyKey { get; set; }
        public int IsCancelled { get; set; }
        public int HasSuccessor { get; set; }
    }

    private sealed class ExecutionRow
    {
        public string InspectionId { get; set; } = string.Empty;
        public string InspectionType { get; set; } = string.Empty;
        public string RelationType { get; set; } = string.Empty;
        public string RootInspectionId { get; set; } = string.Empty;
        public string? ParentInspectionId { get; set; }
        public string LotId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public int LotQty { get; set; }
        public int SampleQty { get; set; }
        public int DefectQty { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public DateTime InspectedAt { get; set; }
        public string InspectorId { get; set; } = string.Empty;
        public string? Result { get; set; }
        public string? Remark { get; set; }
        public string? SamplingSnapshotJson { get; set; }

        public InspectionExecution ToDomain(
            IReadOnlyList<InspectionResult> items,
            IReadOnlyList<InspectionExecutionHistory> history)
        {
            InspectionSamplingPlanSnapshot? snapshot = null;
            if (!string.IsNullOrWhiteSpace(SamplingSnapshotJson))
                snapshot = JsonSerializer.Deserialize<InspectionSamplingPlanSnapshot>(
                    SamplingSnapshotJson, SnapshotJsonOptions);
            return InspectionExecution.Restore(
                InspectionId,
                Enum.Parse<InspectionExecutionType>(InspectionType, true),
                Enum.Parse<InspectionExecutionRelationType>(RelationType, true),
                RootInspectionId,
                ParentInspectionId,
                LotId,
                EquipmentId,
                LotQty,
                SampleQty,
                DefectQty,
                IdempotencyKey,
                RequestHash,
                InspectedAt,
                InspectorId,
                string.Equals(Result, "Pass", StringComparison.OrdinalIgnoreCase),
                Remark,
                snapshot,
                items,
                history);
        }
    }

    private sealed class ResultRow
    {
        public string ResultId { get; set; } = string.Empty;
        public string InspectionId { get; set; } = string.Empty;
        public string? InspectionType { get; set; }
        public string SpecId { get; set; } = string.Empty;
        public string LotId { get; set; } = string.Empty;
        public string EquipmentId { get; set; } = string.Empty;
        public decimal? MeasuredValue { get; set; }
        public string? AttributeResult { get; set; }
        public DateTime InspectedAt { get; set; }
        public string InspectorId { get; set; } = string.Empty;
        public bool IsPass { get; set; }
        public string? Remark { get; set; }
        public int? ItemSequence { get; set; }
        public int? SampleQty { get; set; }
        public int? DefectQty { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime? UpdatedAt { get; set; }

        public InspectionResult ToDomain() => InspectionResult.Restore(
            ResultId, SpecId, LotId, EquipmentId, MeasuredValue, AttributeResult,
            InspectedAt, InspectorId, IsPass, Remark,
            CreatedBy, CreatedAt, UpdatedBy, UpdatedAt,
            Enum.TryParse<InspectionExecutionType>(InspectionType, true, out var type)
                ? type
                : InspectionExecutionType.Process,
            InspectionId,
            SampleQty ?? 1,
            DefectQty ?? (IsPass ? 0 : 1));

    }

    private sealed class HistoryRow
    {
        public string EventId { get; set; } = string.Empty;
        public string InspectionId { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string? RelatedInspectionId { get; set; }
        public string? ParentInspectionId { get; set; }
        public string RootInspectionId { get; set; } = string.Empty;
        public string IdempotencyKey { get; set; } = string.Empty;
        public string RequestHash { get; set; } = string.Empty;
        public string ActorId { get; set; } = string.Empty;
        public string? Reason { get; set; }
        public DateTime OccurredAt { get; set; }

        public InspectionExecutionHistory ToDomain() => new(
            EventId,
            InspectionId,
            Enum.Parse<InspectionExecutionEventType>(EventType, true),
            IdempotencyKey,
            RequestHash,
            ActorId,
            OccurredAt,
            RootInspectionId,
            ParentInspectionId,
            RelatedInspectionId,
            Reason);
    }

    private sealed class SamplingRow
    {
        public string PlanRevisionId { get; set; } = string.Empty;
        public string PlanId { get; set; } = string.Empty;
        public int RevisionNo { get; set; }
        public string InspectionMode { get; set; } = string.Empty;
        public int LotSizeMin { get; set; }
        public int? LotSizeMax { get; set; }
        public int? SampleSize { get; set; }
        public int AcceptanceNo { get; set; }
        public int RejectionNo { get; set; }
        public decimal Aql { get; set; }
        public string StandardName { get; set; } = string.Empty;
        public string StandardVersion { get; set; } = string.Empty;
        public DateTime EffectiveFrom { get; set; }

        public SamplingPlanRevision ToDomain() => new(
            PlanRevisionId, PlanId, RevisionNo,
            Enum.Parse<InspectionSamplingMode>(InspectionMode, true),
            LotSizeMin, LotSizeMax, SampleSize, AcceptanceNo, RejectionNo,
            Aql, StandardName, StandardVersion, EffectiveFrom);
    }
}
