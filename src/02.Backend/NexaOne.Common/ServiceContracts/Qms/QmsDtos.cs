namespace NexaOne.ServiceContracts.Qms;

// Host/plugin boundary DTOs intentionally match the Web API models. Entity identifiers use a
// single `Id` convention so JSON does not silently diverge (DefectId versus Id).
public record DefectDto(
    string Id, string LotId, string EquipmentId, string DefectClassId,
    int DefectCount, decimal DefectRate, DateTime InspectedAt, string InspectorId,
    string? Remark, bool IsConfirmed, DateTime? ConfirmedAt);

public record DefectClassDto(
    string Id, string DefectClassName, string Description, string Severity, bool IsActive);

public record InspectionSpecDto(
    string Id, string SpecName, string ProcessId, string ItemName, string MeasureType,
    decimal? NominalValue, decimal? TolerancePlus, decimal? ToleranceMinus, bool IsActive);

public record InspectionResultDto(
    string Id, string InspectionId, string SpecId, string LotId, string EquipmentId,
    decimal? MeasuredValue, string? AttributeResult, DateTime InspectedAt,
    string InspectorId, bool IsPass, string? Remark);

/// <summary>v2 검사 실행에 포함되는 규격별 입력입니다. ID와 판정은 서버가 생성합니다.</summary>
public record InspectionExecutionItemInputDto(
    string SpecId,
    decimal? MeasuredValue,
    string? AttributeResult,
    int SampleQuantity,
    int DefectQuantity,
    string? Remark = null);

/// <summary>서버 생성 ID를 사용하는 다항목 검사 확정 요청입니다.</summary>
public record RecordInspectionExecutionV2Dto(
    string IdempotencyKey,
    string InspectionType,
    string LotId,
    string EquipmentId,
    int LotQuantity,
    int SampleQuantity,
    int DefectQuantity,
    IReadOnlyList<InspectionExecutionItemInputDto> Items,
    string? SamplingPlanRevisionId = null,
    string? ParentInspectionId = null,
    string RelationType = "Original",
    string? Remark = null);

public record InspectionExecutionItemDto(
    string ResultId,
    string SpecId,
    decimal? MeasuredValue,
    string? AttributeResult,
    int SampleQuantity,
    int DefectQuantity,
    bool IsPass,
    string? Remark);

public record InspectionSamplingPlanSnapshotDto(
    string PlanRevisionId,
    string PlanId,
    int RevisionNo,
    string Mode,
    int LotSizeMin,
    int? LotSizeMax,
    int? SampleSize,
    int AcceptanceNumber,
    int RejectionNumber,
    decimal Aql,
    string StandardName,
    string StandardVersion,
    DateTime EffectiveFrom);

public record InspectionExecutionHistoryDto(
    string EventId,
    string EventType,
    string IdempotencyKey,
    string RequestHash,
    string ActorId,
    DateTime OccurredAt,
    string RootInspectionId,
    string? ParentInspectionId,
    string? RelatedInspectionId,
    string? Reason);

public record InspectionAiEvidenceDto(
    AiInferenceDto Inference,
    IReadOnlyList<AiReviewDto> Reviews);

/// <summary>실행 헤더, 항목, 이력, AI 증적을 한 번에 조회하는 권위 응답입니다.</summary>
public record InspectionExecutionV2Dto(
    string InspectionId,
    string InspectionType,
    string RelationType,
    string RootInspectionId,
    string? ParentInspectionId,
    string LotId,
    string EquipmentId,
    int LotQuantity,
    int SampleQuantity,
    int DefectQuantity,
    string IdempotencyKey,
    string RequestHash,
    DateTime InspectedAt,
    string InspectorId,
    bool IsPass,
    bool IsCancelled,
    bool IsReplay,
    string? Remark,
    InspectionSamplingPlanSnapshotDto? SamplingPlan,
    IReadOnlyList<InspectionExecutionItemDto> Items,
    IReadOnlyList<InspectionExecutionHistoryDto> History,
    IReadOnlyList<InspectionAiEvidenceDto> AiEvidence);

public record CancelInspectionExecutionV2Dto(string IdempotencyKey, string Reason);

public record SpcParamDto(
    string Id, string ParamName, string EquipmentId, string ProcessId,
    decimal Mean, decimal Ucl, decimal Lcl, decimal? Usl, decimal? Lsl,
    int SampleSize, bool IsActive);

/// <summary>Read-only quality gate contract for production/lot consumers.</summary>
public record LotInspectionStatusDto(
    string LotId, bool HasResults, bool AllPassed, int ResultCount, int FailedCount,
    DateTime? LastInspectedAt);

public record SpcLimitRevisionDto(
    string Id, string ParamId, int RevisionNo, string ChartType,
    decimal CenterLine, decimal Ucl, decimal Lcl, DateTime EffectiveFrom, string Reason);
public record SpcRuleViolationDto(
    string Id, string ParamId, string LimitRevisionId, string ObservationId,
    string RuleCode, DateTime DetectedAt, string Evidence);
public record SpcSubgroupEvaluationDto(
    string SubgroupId, string ParamId, string LimitRevisionId, string ChartType,
    DateTime ObservedAt, IReadOnlyList<decimal> Values,
    IReadOnlyList<SpcRuleViolationDto> Violations, bool IsReplay);

public record SamplingPlanRevisionDto(
    string Id, string PlanId, int RevisionNo, string Mode, int LotSizeMin,
    int? LotSizeMax, int? SampleSize, int AcceptanceNumber, int RejectionNumber,
    decimal Aql, string StandardName, string StandardVersion, DateTime EffectiveFrom);
public record SamplingDecisionDto(
    string Disposition, int RequiredSampleSize, int InspectedQuantity,
    int DefectQuantity, string Reason);
public record SamplingEvaluationDto(SamplingPlanRevisionDto Plan, SamplingDecisionDto Decision);

public record AiModelVersionDto(
    string Id, string ModelId, int VersionNo, string ArtifactUri,
    string ArtifactSha256, decimal ConfidenceThreshold, DateTime EffectiveFrom);
public record AiInferenceDto(
    string Id, string IdempotencyKey, string ModelVersionId, string InspectionId,
    string ImageUri, string ImageSha256, string RawVerdict, decimal Confidence,
    decimal Threshold, DateTime InferredAt, bool RequiresReview);
public record AiReviewDto(
    string Id, string InferenceId, int ReviewSequence, string ReviewerId,
    string Verdict, string Reason, DateTime ReviewedAt);
