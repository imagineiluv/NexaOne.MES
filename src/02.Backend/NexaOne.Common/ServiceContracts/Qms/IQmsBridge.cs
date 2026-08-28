using NexaOne.Common;

namespace NexaOne.ServiceContracts.Qms;

public interface IQmsBridge : INexaModuleBridge
{
    Task<IReadOnlyList<DefectDto>> GetDefectsByLotAsync(string lotId, CancellationToken ct = default);
    Task<Result<DefectDto>> RecordDefectAsync(
        string id, string lotId, string equipmentId, string defectClassId,
        int defectCount, decimal defectRate, string inspectorId, string? remark,
        CancellationToken ct = default);
    Task<Result> ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default);

    Task<IReadOnlyList<DefectClassDto>> GetDefectClassesAsync(CancellationToken ct = default);
    Task<Result<DefectClassDto>> CreateDefectClassAsync(
        string id, string name, string description, string severity, CancellationToken ct = default);

    Task<IReadOnlyList<InspectionSpecDto>> GetInspectionSpecsAsync(
        string? processId = null, CancellationToken ct = default);
    Task<Result<InspectionSpecDto>> CreateInspectionSpecAsync(
        string id, string name, string processId, string itemName, string measureType,
        decimal? nominalValue, decimal? tolerancePlus, decimal? toleranceMinus,
        CancellationToken ct = default);

    Task<IReadOnlyList<InspectionResultDto>> GetInspectionResultsByLotAsync(
        string lotId, CancellationToken ct = default);
    Task<Result<InspectionResultDto>> RecordInspectionResultAsync(
        string id, string specId, string lotId, string equipmentId, string inspectorId,
        decimal? measuredValue, string? attributeResult, bool? isPass, string? remark,
        CancellationToken ct = default);
    /// <summary>업무 화면이 지정한 Incoming/Process/Shipping 유형으로 검사 결과를 기록합니다.</summary>
    Task<Result<InspectionResultDto>> RecordInspectionExecutionAsync(
        string inspectionType, string id, string specId, string lotId, string equipmentId,
        string inspectorId, decimal? measuredValue, string? attributeResult,
        bool? isPass, string? remark, CancellationToken ct = default)
        => RecordInspectionResultAsync(id, specId, lotId, equipmentId, inspectorId,
            measuredValue, attributeResult, isPass, remark, ct);
    Task<LotInspectionStatusDto> GetLotInspectionStatusAsync(string lotId, CancellationToken ct = default);

    /// <summary>한 헤더와 여러 규격 결과를 원자 확정하는 권위 v2 경계입니다.</summary>
    /// <remarks>
    /// Persists header, ordered items, confirmation, and optional lineage event atomically.
    /// Reusing the same idempotency key with the same canonical request returns the original
    /// execution with <c>IsReplay=true</c>; different request semantics return Conflict.
    /// </remarks>
    Task<Result<InspectionExecutionV2Dto>> RecordInspectionExecutionV2Async(
        RecordInspectionExecutionV2Dto request,
        string actorId,
        CancellationToken ct = default)
        => Task.FromResult(Result.Failure<InspectionExecutionV2Dto>(Error.Failure(
            "QMS_V2_UNAVAILABLE", "The QMS v2 inspection bridge is unavailable.")));

    /// <summary>
    /// Reads the immutable v2 aggregate, append-only lifecycle history, and linked AI evidence.
    /// The returned <c>IsReplay</c> flag is false because this operation does not perform a write.
    /// </summary>
    Task<Result<InspectionExecutionV2Dto>> GetInspectionExecutionV2Async(
        string inspectionId,
        CancellationToken ct = default)
        => Task.FromResult(Result.Failure<InspectionExecutionV2Dto>(Error.Failure(
            "QMS_V2_UNAVAILABLE", "The QMS v2 inspection bridge is unavailable.")));

    /// <summary>
    /// Appends a cancellation event without updating/deleting confirmed header or result rows.
    /// The idempotency key plus canonical cancellation hash replays the same request with
    /// <c>IsReplay=true</c>; key reuse with different semantics or a second cancellation conflicts.
    /// </summary>
    Task<Result<InspectionExecutionV2Dto>> CancelInspectionExecutionV2Async(
        string inspectionId,
        string idempotencyKey,
        string reason,
        string actorId,
        CancellationToken ct = default)
        => Task.FromResult(Result.Failure<InspectionExecutionV2Dto>(Error.Failure(
            "QMS_V2_UNAVAILABLE", "The QMS v2 inspection bridge is unavailable.")));

    Task<IReadOnlyList<SpcParamDto>> GetSpcParamsAsync(string equipmentId, CancellationToken ct = default);
    Task<Result<SpcParamDto>> CreateSpcParamAsync(
        string id, string name, string equipmentId, string processId,
        decimal mean, decimal ucl, decimal lcl, int sampleSize,
        decimal? usl, decimal? lsl, CancellationToken ct = default);
    Task<Result> UpdateControlLimitsAsync(
        string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default);

    Task<Result<SpcLimitRevisionDto>> AddSpcLimitRevisionAsync(
        string id, string paramId, int revisionNo, string chartType, decimal centerLine,
        decimal ucl, decimal lcl, DateTime effectiveFrom, string reason, CancellationToken ct = default);
    Task<Result<SpcSubgroupEvaluationDto>> EvaluateSpcSubgroupAsync(
        string subgroupId, string idempotencyKey, string limitRevisionId, DateTime observedAt,
        IReadOnlyList<decimal> values, string sourceType, string actorId, CancellationToken ct = default);
    Task<IReadOnlyList<SpcRuleViolationDto>> GetSpcViolationsAsync(
        string? paramId, string? subgroupId, CancellationToken ct = default);

    Task<Result<SamplingPlanRevisionDto>> AddSamplingPlanRevisionAsync(
        string id, string planId, int revisionNo, string mode, int lotSizeMin, int? lotSizeMax,
        int? sampleSize, int acceptanceNumber, int rejectionNumber, decimal aql,
        string standardName, string standardVersion, DateTime effectiveFrom, CancellationToken ct = default);
    Task<Result<SamplingPlanRevisionDto>> SelectSamplingPlanAsync(
        int lotSize, DateTime effectiveAt, CancellationToken ct = default);
    Task<Result<SamplingEvaluationDto>> EvaluateSamplingAsync(
        int lotSize, int inspectedQuantity, int defectQuantity, DateTime effectiveAt,
        CancellationToken ct = default);

    Task<Result<AiModelVersionDto>> RegisterAiModelVersionAsync(
        string id, string modelId, int versionNo, string artifactUri, string artifactSha256,
        decimal confidenceThreshold, DateTime effectiveFrom, CancellationToken ct = default);
    Task<Result<AiInferenceDto>> RecordAiInferenceAsync(
        string id, string idempotencyKey, string modelVersionId, string inspectionId,
        string imageUri, string imageSha256, string rawVerdict, decimal confidence,
        DateTime inferredAt, CancellationToken ct = default);
    Task<Result<AiInferenceDto>> GetAiInferenceAsync(string inferenceId, CancellationToken ct = default);
    Task<IReadOnlyList<AiReviewDto>> GetAiReviewsAsync(string inferenceId, CancellationToken ct = default);
    Task<Result<AiReviewDto>> ReviewAiInferenceAsync(
        string reviewId, string inferenceId, string reviewerId, string verdict,
        string reason, DateTime reviewedAt, CancellationToken ct = default);
}
