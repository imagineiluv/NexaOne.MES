using NexaOne.Common;

namespace NexaOne.QMS.Domain;

/// <summary>확정 검사가 이전 검사를 대체하거나 다시 수행한 이유를 나타냅니다.</summary>
public enum InspectionExecutionRelationType
{
    Original,
    Correction,
    Reinspection
}

/// <summary>검사 실행에 append-only로 기록되는 감사 사건입니다.</summary>
public enum InspectionExecutionEventType
{
    Confirmed,
    Cancelled,
    Corrected,
    Reinspected
}

/// <summary>검사 시점의 샘플링 계획을 이후 개정과 무관하게 재현하기 위한 스냅샷입니다.</summary>
public sealed record InspectionSamplingPlanSnapshot(
    string PlanRevisionId,
    string PlanId,
    int RevisionNo,
    InspectionSamplingMode Mode,
    int LotSizeMin,
    int? LotSizeMax,
    int? SampleSize,
    int AcceptanceNumber,
    int RejectionNumber,
    decimal Aql,
    string StandardName,
    string StandardVersion,
    DateTime EffectiveFrom)
{
    public static InspectionSamplingPlanSnapshot FromRevision(SamplingPlanRevision revision) => new(
        revision.PlanRevisionId, revision.PlanId, revision.RevisionNo, revision.Mode,
        revision.LotSizeMin, revision.LotSizeMax, revision.SampleSize,
        revision.AcceptanceNumber, revision.RejectionNumber, revision.Aql,
        revision.StandardName, revision.StandardVersion, revision.EffectiveFrom);
}

/// <summary>확정 행을 수정하지 않고 취소·정정·재검 관계를 남기는 감사 이력입니다.</summary>
public sealed record InspectionExecutionHistory(
    string EventId,
    string InspectionId,
    InspectionExecutionEventType EventType,
    string IdempotencyKey,
    string RequestHash,
    string ActorId,
    DateTime OccurredAt,
    string RootInspectionId,
    string? ParentInspectionId,
    string? RelatedInspectionId,
    string? Reason)
{
    public static Result<InspectionExecutionHistory> Create(
        string eventId,
        string inspectionId,
        InspectionExecutionEventType eventType,
        string idempotencyKey,
        string requestHash,
        string actorId,
        DateTime occurredAt,
        string rootInspectionId,
        string? parentInspectionId = null,
        string? relatedInspectionId = null,
        string? reason = null)
    {
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(inspectionId))
            return Result.Failure<InspectionExecutionHistory>(Error.Validation(
                nameof(eventId), "Event and inspection IDs are required."));
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 150)
            return Result.Failure<InspectionExecutionHistory>(Error.Validation(
                nameof(idempotencyKey), "An idempotency key of at most 150 characters is required."));
        if (requestHash is not { Length: 64 } || !requestHash.All(Uri.IsHexDigit))
            return Result.Failure<InspectionExecutionHistory>(Error.Validation(
                nameof(requestHash), "A canonical SHA-256 request hash is required."));
        if (string.IsNullOrWhiteSpace(actorId) || string.IsNullOrWhiteSpace(rootInspectionId))
            return Result.Failure<InspectionExecutionHistory>(Error.Validation(
                nameof(actorId), "Actor and root inspection IDs are required."));
        if (occurredAt == default)
            return Result.Failure<InspectionExecutionHistory>(Error.Validation(
                nameof(occurredAt), "Event time is required."));

        return new InspectionExecutionHistory(
            eventId.Trim(), inspectionId.Trim(), eventType, idempotencyKey.Trim(),
            requestHash.ToLowerInvariant(), actorId.Trim(), occurredAt,
            rootInspectionId.Trim(), Normalize(parentInspectionId),
            Normalize(relatedInspectionId), Normalize(reason));
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// 하나의 검사 헤더와 여러 검사 항목을 함께 확정하는 권위 집계입니다.
/// 생성 후 결과는 변경하지 않으며 취소·정정·재검은 <see cref="History"/>로만 표현합니다.
/// </summary>
public sealed class InspectionExecution
{
    private InspectionExecution() { }

    public string InspectionId { get; private init; } = string.Empty;
    public InspectionExecutionType InspectionType { get; private init; }
    public InspectionExecutionRelationType RelationType { get; private init; }
    public string RootInspectionId { get; private init; } = string.Empty;
    public string? ParentInspectionId { get; private init; }
    public string LotId { get; private init; } = string.Empty;
    public string EquipmentId { get; private init; } = string.Empty;
    public int LotQuantity { get; private init; }
    public int SampleQuantity { get; private init; }
    public int DefectQuantity { get; private init; }
    public string IdempotencyKey { get; private init; } = string.Empty;
    public string RequestHash { get; private init; } = string.Empty;
    public DateTime InspectedAt { get; private init; }
    public string InspectorId { get; private init; } = string.Empty;
    public bool IsPass { get; private init; }
    public string? Remark { get; private init; }
    public InspectionSamplingPlanSnapshot? SamplingPlan { get; private init; }
    public IReadOnlyList<InspectionResult> Items { get; private init; } = [];
    public IReadOnlyList<InspectionExecutionHistory> History { get; private init; } = [];
    public bool IsCancelled => History.Any(x => x.EventType == InspectionExecutionEventType.Cancelled);

    /// <summary>
    /// Creates and validates an authoritative inspection aggregate. It enforces valid relation
    /// shape, positive lot/sample quantities, bounded defect quantities, a canonical SHA-256
    /// request hash, at least one item, unique specifications, and item ownership by the same
    /// inspection/lot/equipment. The final verdict requires both sampling acceptance and every
    /// item-level verdict to pass.
    /// </summary>
    public static Result<InspectionExecution> Create(
        string inspectionId,
        InspectionExecutionType inspectionType,
        InspectionExecutionRelationType relationType,
        string rootInspectionId,
        string? parentInspectionId,
        string lotId,
        string equipmentId,
        int lotQuantity,
        int sampleQuantity,
        int defectQuantity,
        string idempotencyKey,
        string requestHash,
        DateTime inspectedAt,
        string inspectorId,
        IReadOnlyList<InspectionResult> items,
        InspectionSamplingPlanSnapshot? samplingPlan,
        bool samplingAccepted,
        string? remark)
    {
        if (string.IsNullOrWhiteSpace(inspectionId) || string.IsNullOrWhiteSpace(rootInspectionId))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(inspectionId), "Inspection and root IDs are required."));
        if (!Enum.IsDefined(inspectionType) || !Enum.IsDefined(relationType))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(inspectionType), "Inspection and relation types are invalid."));
        if (relationType == InspectionExecutionRelationType.Original && !string.IsNullOrWhiteSpace(parentInspectionId))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(parentInspectionId), "An original inspection cannot have a parent."));
        if (relationType != InspectionExecutionRelationType.Original && string.IsNullOrWhiteSpace(parentInspectionId))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(parentInspectionId), "Correction and reinspection require a parent inspection."));
        if (string.IsNullOrWhiteSpace(lotId) || string.IsNullOrWhiteSpace(equipmentId)
            || string.IsNullOrWhiteSpace(inspectorId))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(lotId), "Lot, equipment, and inspector IDs are required."));
        if (lotQuantity <= 0 || sampleQuantity <= 0 || sampleQuantity > lotQuantity
            || defectQuantity < 0 || defectQuantity > sampleQuantity)
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(sampleQuantity), "Lot/sample/defect quantities are invalid."));
        if (string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Trim().Length > 150)
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(idempotencyKey), "An idempotency key of at most 150 characters is required."));
        if (!IsSha256(requestHash))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(requestHash), "A canonical SHA-256 request hash is required."));
        if (inspectedAt == default)
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(inspectedAt), "Inspection time is required."));
        if (items is null || items.Count == 0)
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(items), "At least one inspection item is required."));
        if (items.Any(x => x.InspectionId != inspectionId || x.LotId != lotId || x.EquipmentId != equipmentId))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(items), "Every item must belong to the same inspection, lot, and equipment."));
        if (items.Select(x => x.SpecId).Distinct(StringComparer.OrdinalIgnoreCase).Count() != items.Count)
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(items), "An inspection specification can appear only once per execution."));
        if (items.Any(x => x.SampleQuantity > sampleQuantity || x.DefectQuantity > defectQuantity))
            return Result.Failure<InspectionExecution>(Error.Validation(
                nameof(items), "Item quantities cannot exceed the execution quantities."));

        return new InspectionExecution
        {
            InspectionId = inspectionId.Trim(),
            InspectionType = inspectionType,
            RelationType = relationType,
            RootInspectionId = rootInspectionId.Trim(),
            ParentInspectionId = Normalize(parentInspectionId),
            LotId = lotId.Trim(),
            EquipmentId = equipmentId.Trim(),
            LotQuantity = lotQuantity,
            SampleQuantity = sampleQuantity,
            DefectQuantity = defectQuantity,
            IdempotencyKey = idempotencyKey.Trim(),
            RequestHash = requestHash.ToLowerInvariant(),
            InspectedAt = inspectedAt,
            InspectorId = inspectorId.Trim(),
            IsPass = samplingAccepted && items.All(x => x.IsPass),
            Remark = Normalize(remark),
            SamplingPlan = samplingPlan,
            Items = items.ToArray()
        };
    }

    /// <summary>
    /// Rehydrates an aggregate from trusted persistence without rerunning creation validation.
    /// This method deliberately bypasses invariants because database constraints/triggers are
    /// the trust boundary; request handlers must always use <see cref="Create"/> instead.
    /// </summary>
    public static InspectionExecution Restore(
        string inspectionId,
        InspectionExecutionType inspectionType,
        InspectionExecutionRelationType relationType,
        string rootInspectionId,
        string? parentInspectionId,
        string lotId,
        string equipmentId,
        int lotQuantity,
        int sampleQuantity,
        int defectQuantity,
        string idempotencyKey,
        string requestHash,
        DateTime inspectedAt,
        string inspectorId,
        bool isPass,
        string? remark,
        InspectionSamplingPlanSnapshot? samplingPlan,
        IReadOnlyList<InspectionResult> items,
        IReadOnlyList<InspectionExecutionHistory> history) => new()
    {
        InspectionId = inspectionId,
        InspectionType = inspectionType,
        RelationType = relationType,
        RootInspectionId = rootInspectionId,
        ParentInspectionId = parentInspectionId,
        LotId = lotId,
        EquipmentId = equipmentId,
        LotQuantity = lotQuantity,
        SampleQuantity = sampleQuantity,
        DefectQuantity = defectQuantity,
        IdempotencyKey = idempotencyKey,
        RequestHash = requestHash,
        InspectedAt = inspectedAt,
        InspectorId = inspectorId,
        IsPass = isPass,
        Remark = remark,
        SamplingPlan = samplingPlan,
        Items = items,
        History = history
    };

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsSha256(string value)
        => value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
