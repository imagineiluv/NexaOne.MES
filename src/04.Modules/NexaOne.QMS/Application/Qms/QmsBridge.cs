using NexaOne.Common;
using NexaOne.QMS.Domain;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.QMS.Application.Qms;

public sealed class QmsBridge : IQmsBridge
{
    private readonly QmsService _service;
    private readonly AdvancedQualityService _advanced;
    private readonly AiInspectionService _ai;
    public QmsBridge(QmsService service, AdvancedQualityService advanced, AiInspectionService ai)
    {
        _service = service;
        _advanced = advanced;
        _ai = ai;
    }

    public async Task<IReadOnlyList<DefectDto>> GetDefectsByLotAsync(string lotId, CancellationToken ct = default)
    {
        var result = await _service.GetDefectsByLotAsync(lotId, ct);
        return result.IsSuccess ? result.Value.Select(ToDto).ToList() : [];
    }

    public async Task<Result<DefectDto>> RecordDefectAsync(
        string id, string lotId, string equipmentId, string defectClassId,
        int defectCount, decimal defectRate, string inspectorId, string? remark,
        CancellationToken ct = default)
        => Map(await _service.RecordDefectAsync(id, lotId, equipmentId, defectClassId,
            defectCount, defectRate, inspectorId, remark, ct), ToDto);

    public Task<Result> ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default)
        => _service.ConfirmDefectAsync(defectId, confirmerId, ct);

    public async Task<IReadOnlyList<DefectClassDto>> GetDefectClassesAsync(CancellationToken ct = default)
        => (await _service.GetDefectClassesAsync(ct)).Select(ToDto).ToList();

    public async Task<Result<DefectClassDto>> CreateDefectClassAsync(
        string id, string name, string description, string severity, CancellationToken ct = default)
        => Map(await _service.CreateDefectClassAsync(id, name, description, severity, ct), ToDto);

    public async Task<IReadOnlyList<InspectionSpecDto>> GetInspectionSpecsAsync(
        string? processId = null, CancellationToken ct = default)
        => (await _service.GetInspectionSpecsAsync(processId, ct)).Select(ToDto).ToList();

    public async Task<Result<InspectionSpecDto>> CreateInspectionSpecAsync(
        string id, string name, string processId, string itemName, string measureType,
        decimal? nominalValue, decimal? tolerancePlus, decimal? toleranceMinus,
        CancellationToken ct = default)
        => Map(await _service.CreateInspectionSpecAsync(id, name, processId, itemName, measureType,
            nominalValue, tolerancePlus, toleranceMinus, ct), ToDto);

    public async Task<IReadOnlyList<InspectionResultDto>> GetInspectionResultsByLotAsync(
        string lotId, CancellationToken ct = default)
        => (await _service.GetInspectionResultsByLotAsync(lotId, ct)).Select(ToDto).ToList();

    public async Task<Result<InspectionResultDto>> RecordInspectionResultAsync(
        string id, string specId, string lotId, string equipmentId, string inspectorId,
        decimal? measuredValue, string? attributeResult, bool? isPass, string? remark,
        CancellationToken ct = default)
        => Map(await _service.RecordInspectionResultAsync(id, specId, lotId, equipmentId,
            inspectorId, measuredValue, attributeResult, isPass, remark, ct), ToDto);

    /// <summary>등록 화면의 업무 유형을 유지하면서 동일한 검사 도메인 검증과 원자 저장을 수행합니다.</summary>
    public async Task<Result<InspectionResultDto>> RecordInspectionExecutionAsync(
        string inspectionType, string id, string specId, string lotId, string equipmentId,
        string inspectorId, decimal? measuredValue, string? attributeResult,
        bool? isPass, string? remark, CancellationToken ct = default)
        => Map(await _service.RecordInspectionExecutionAsync(inspectionType, id, specId,
            lotId, equipmentId, inspectorId, measuredValue, attributeResult,
            isPass, remark, ct), ToDto);

    public async Task<LotInspectionStatusDto> GetLotInspectionStatusAsync(
        string lotId, CancellationToken ct = default)
    {
        var status = await _service.GetEffectiveLotInspectionStatusAsync(lotId, ct);
        return new LotInspectionStatusDto(lotId, status.HasResults, status.AllPassed,
            status.ResultCount, status.FailedCount, status.LastInspectedAt);
    }

    public async Task<Result<InspectionExecutionV2Dto>> RecordInspectionExecutionV2Async(
        RecordInspectionExecutionV2Dto request,
        string actorId,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<InspectionExecutionType>(request.InspectionType, true, out var inspectionType)
            || !Enum.IsDefined(inspectionType))
            return Result.Failure<InspectionExecutionV2Dto>(Error.Validation(
                nameof(request.InspectionType), "Inspection type must be Incoming, Process, or Shipping."));
        if (!Enum.TryParse<InspectionExecutionRelationType>(request.RelationType, true, out var relationType)
            || !Enum.IsDefined(relationType))
            return Result.Failure<InspectionExecutionV2Dto>(Error.Validation(
                nameof(request.RelationType), "Relation type must be Original, Correction, or Reinspection."));

        var command = new RecordInspectionExecutionCommand(
            request.IdempotencyKey,
            inspectionType,
            relationType,
            request.ParentInspectionId,
            request.LotId,
            request.EquipmentId,
            request.LotQuantity,
            request.SampleQuantity,
            request.DefectQuantity,
            request.SamplingPlanRevisionId,
            (request.Items ?? []).Select(x => new InspectionExecutionItemCommand(
                x.SpecId, x.MeasuredValue, x.AttributeResult,
                x.SampleQuantity, x.DefectQuantity, x.Remark)).ToArray(),
            request.Remark);
        var result = await _service.RecordInspectionExecutionV2Async(command, actorId, ct);
        return await MapExecutionAsync(result, ct);
    }

    public async Task<Result<InspectionExecutionV2Dto>> GetInspectionExecutionV2Async(
        string inspectionId, CancellationToken ct = default)
        => await MapExecutionAsync(
            await _service.GetInspectionExecutionV2Async(inspectionId, ct), ct);

    public async Task<Result<InspectionExecutionV2Dto>> CancelInspectionExecutionV2Async(
        string inspectionId,
        string idempotencyKey,
        string reason,
        string actorId,
        CancellationToken ct = default)
        => await MapExecutionAsync(
            await _service.CancelInspectionExecutionV2Async(
                inspectionId, idempotencyKey, reason, actorId, ct), ct);

    public async Task<IReadOnlyList<SpcParamDto>> GetSpcParamsAsync(
        string equipmentId, CancellationToken ct = default)
        => (await _service.GetSpcParamsAsync(equipmentId, ct)).Select(ToDto).ToList();

    public async Task<Result<SpcParamDto>> CreateSpcParamAsync(
        string id, string name, string equipmentId, string processId,
        decimal mean, decimal ucl, decimal lcl, int sampleSize,
        decimal? usl, decimal? lsl, CancellationToken ct = default)
        => Map(await _service.CreateSpcParamAsync(id, name, equipmentId, processId,
            mean, ucl, lcl, sampleSize, usl, lsl, ct), ToDto);

    public Task<Result> UpdateControlLimitsAsync(
        string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default)
        => _service.UpdateSpcControlLimitsAsync(paramId, mean, ucl, lcl, ct);

    public async Task<Result<SpcLimitRevisionDto>> AddSpcLimitRevisionAsync(
        string id, string paramId, int revisionNo, string chartType, decimal centerLine,
        decimal ucl, decimal lcl, DateTime effectiveFrom, string reason, CancellationToken ct = default)
    {
        if (!Enum.TryParse<SpcControlChartType>(chartType, true, out var parsed))
            return Result.Failure<SpcLimitRevisionDto>(Error.Validation(nameof(chartType), "Unsupported SPC chart type."));
        return Map(await _advanced.AddLimitRevisionAsync(id, paramId, revisionNo, parsed,
            centerLine, ucl, lcl, effectiveFrom, reason, ct), ToDto);
    }

    public async Task<Result<SpcSubgroupEvaluationDto>> EvaluateSpcSubgroupAsync(
        string subgroupId, string idempotencyKey, string limitRevisionId, DateTime observedAt,
        IReadOnlyList<decimal> values, string sourceType, string actorId, CancellationToken ct = default)
        => Map(await _advanced.EvaluateSubgroupAsync(subgroupId, idempotencyKey,
            limitRevisionId, observedAt, values, sourceType, actorId, ct), ToDto);

    public async Task<IReadOnlyList<SpcRuleViolationDto>> GetSpcViolationsAsync(
        string? paramId, string? subgroupId, CancellationToken ct = default)
        => (await _advanced.GetViolationsAsync(paramId, subgroupId, ct)).Select(ToDto).ToList();

    public async Task<Result<SamplingPlanRevisionDto>> AddSamplingPlanRevisionAsync(
        string id, string planId, int revisionNo, string mode, int lotSizeMin, int? lotSizeMax,
        int? sampleSize, int acceptanceNumber, int rejectionNumber, decimal aql,
        string standardName, string standardVersion, DateTime effectiveFrom, CancellationToken ct = default)
    {
        if (!Enum.TryParse<InspectionSamplingMode>(mode, true, out var parsed))
            return Result.Failure<SamplingPlanRevisionDto>(Error.Validation(nameof(mode), "Sampling mode must be Full or Sampling."));
        return Map(await _advanced.AddSamplingPlanRevisionAsync(id, planId, revisionNo, parsed,
            lotSizeMin, lotSizeMax, sampleSize, acceptanceNumber, rejectionNumber, aql,
            standardName, standardVersion, effectiveFrom, ct), ToDto);
    }

    public async Task<Result<SamplingPlanRevisionDto>> SelectSamplingPlanAsync(
        int lotSize, DateTime effectiveAt, CancellationToken ct = default)
    {
        var plan = await _advanced.SelectSamplingPlanAsync(lotSize, effectiveAt, ct);
        return plan is null
            ? Result.Failure<SamplingPlanRevisionDto>(Error.NotFoundOf(nameof(SamplingPlanRevision), lotSize.ToString()))
            : Result.Success(ToDto(plan));
    }

    public async Task<Result<SamplingEvaluationDto>> EvaluateSamplingAsync(
        int lotSize, int inspectedQuantity, int defectQuantity, DateTime effectiveAt,
        CancellationToken ct = default)
    {
        var plan = await _advanced.SelectSamplingPlanAsync(lotSize, effectiveAt, ct);
        if (plan is null)
            return Result.Failure<SamplingEvaluationDto>(Error.NotFoundOf(nameof(SamplingPlanRevision), lotSize.ToString()));
        var decision = SamplingPlanCalculator.Evaluate(plan, lotSize, inspectedQuantity, defectQuantity);
        return decision.IsFailure
            ? Result.Failure<SamplingEvaluationDto>(decision.Error)
            : Result.Success(new SamplingEvaluationDto(ToDto(plan), ToDto(decision.Value)));
    }

    public async Task<Result<AiModelVersionDto>> RegisterAiModelVersionAsync(
        string id, string modelId, int versionNo, string artifactUri, string artifactSha256,
        decimal confidenceThreshold, DateTime effectiveFrom, CancellationToken ct = default)
        => Map(await _ai.RegisterModelVersionAsync(id, modelId, versionNo, artifactUri,
            artifactSha256, confidenceThreshold, effectiveFrom, ct), ToDto);

    public async Task<Result<AiInferenceDto>> RecordAiInferenceAsync(
        string id, string idempotencyKey, string modelVersionId, string inspectionId,
        string imageUri, string imageSha256, string rawVerdict, decimal confidence,
        DateTime inferredAt, CancellationToken ct = default)
    {
        if (!Enum.TryParse<AiRawVerdict>(rawVerdict, true, out var parsed))
            return Result.Failure<AiInferenceDto>(Error.Validation(nameof(rawVerdict), "AI verdict must be Pass, Fail, or Unknown."));
        return Map(await _ai.RecordInferenceAsync(id, idempotencyKey, modelVersionId,
            inspectionId, imageUri, imageSha256, parsed, confidence, inferredAt, ct), ToDto);
    }

    public async Task<Result<AiInferenceDto>> GetAiInferenceAsync(
        string inferenceId, CancellationToken ct = default)
        => Map(await _ai.GetInferenceAsync(inferenceId, ct), ToDto);

    public async Task<IReadOnlyList<AiReviewDto>> GetAiReviewsAsync(
        string inferenceId, CancellationToken ct = default)
        => (await _ai.GetReviewsAsync(inferenceId, ct)).Select(ToDto).ToList();

    public async Task<Result<AiReviewDto>> ReviewAiInferenceAsync(
        string reviewId, string inferenceId, string reviewerId, string verdict,
        string reason, DateTime reviewedAt, CancellationToken ct = default)
    {
        if (!Enum.TryParse<AiReviewVerdict>(verdict, true, out var parsed))
            return Result.Failure<AiReviewDto>(Error.Validation(nameof(verdict), "Review verdict must be Pass or Fail."));
        return Map(await _ai.ReviewAsync(reviewId, inferenceId, reviewerId,
            parsed, reason, reviewedAt, ct), ToDto);
    }

    private static Result<TDto> Map<TDomain, TDto>(Result<TDomain> result, Func<TDomain, TDto> map)
        => result.IsSuccess ? Result.Success(map(result.Value)) : Result.Failure<TDto>(result.Error);

    private async Task<Result<InspectionExecutionV2Dto>> MapExecutionAsync(
        Result<InspectionExecutionOutcome> result,
        CancellationToken ct)
    {
        if (result.IsFailure)
            return Result.Failure<InspectionExecutionV2Dto>(result.Error);

        var execution = result.Value.Execution;
        var evidence = new List<InspectionAiEvidenceDto>();
        foreach (var inference in await _ai.GetInferencesByInspectionAsync(execution.InspectionId, ct))
        {
            var reviews = await _ai.GetReviewsAsync(inference.InferenceId, ct);
            evidence.Add(new InspectionAiEvidenceDto(
                ToDto(inference), reviews.Select(ToDto).ToArray()));
        }

        return Result.Success(ToDto(execution, result.Value.IsReplay, evidence));
    }

    private static DefectDto ToDto(Defect d) =>
        new(d.Id, d.LotId, d.EquipmentId, d.DefectClassId, d.DefectCount, d.DefectRate,
            d.InspectedAt, d.InspectorId, d.Remark, d.IsConfirmed, d.ConfirmedAt);

    private static DefectClassDto ToDto(DefectClass d) =>
        new(d.Id, d.DefectClassName, d.Description, d.Severity, d.IsActive);

    private static InspectionSpecDto ToDto(InspectionSpec s) =>
        new(s.Id, s.SpecName, s.ProcessId, s.ItemName, s.MeasureType,
            s.NominalValue, s.TolerancePlus, s.ToleranceMinus, s.IsActive);

    private static InspectionResultDto ToDto(InspectionResult r) =>
        new(r.Id, r.InspectionId, r.SpecId, r.LotId, r.EquipmentId, r.MeasuredValue,
            r.AttributeResult, r.InspectedAt, r.InspectorId, r.IsPass, r.Remark);

    private static InspectionExecutionV2Dto ToDto(
        InspectionExecution execution,
        bool isReplay,
        IReadOnlyList<InspectionAiEvidenceDto> evidence)
        => new(
            execution.InspectionId,
            execution.InspectionType.ToString(),
            execution.RelationType.ToString(),
            execution.RootInspectionId,
            execution.ParentInspectionId,
            execution.LotId,
            execution.EquipmentId,
            execution.LotQuantity,
            execution.SampleQuantity,
            execution.DefectQuantity,
            execution.IdempotencyKey,
            execution.RequestHash,
            execution.InspectedAt,
            execution.InspectorId,
            execution.IsPass,
            execution.IsCancelled,
            isReplay,
            execution.Remark,
            execution.SamplingPlan is null ? null : new InspectionSamplingPlanSnapshotDto(
                execution.SamplingPlan.PlanRevisionId,
                execution.SamplingPlan.PlanId,
                execution.SamplingPlan.RevisionNo,
                execution.SamplingPlan.Mode.ToString(),
                execution.SamplingPlan.LotSizeMin,
                execution.SamplingPlan.LotSizeMax,
                execution.SamplingPlan.SampleSize,
                execution.SamplingPlan.AcceptanceNumber,
                execution.SamplingPlan.RejectionNumber,
                execution.SamplingPlan.Aql,
                execution.SamplingPlan.StandardName,
                execution.SamplingPlan.StandardVersion,
                execution.SamplingPlan.EffectiveFrom),
            execution.Items.Select(x => new InspectionExecutionItemDto(
                x.Id, x.SpecId, x.MeasuredValue, x.AttributeResult,
                x.SampleQuantity, x.DefectQuantity, x.IsPass, x.Remark)).ToArray(),
            execution.History.Select(x => new InspectionExecutionHistoryDto(
                x.EventId, x.EventType.ToString(), x.IdempotencyKey, x.RequestHash,
                x.ActorId, x.OccurredAt, x.RootInspectionId, x.ParentInspectionId,
                x.RelatedInspectionId, x.Reason)).ToArray(),
            evidence);

    private static SpcParamDto ToDto(SpcParam p) =>
        new(p.Id, p.ParamName, p.EquipmentId, p.ProcessId, p.Mean, p.Ucl, p.Lcl,
            p.Usl, p.Lsl, p.SampleSize, p.IsActive);

    private static SpcLimitRevisionDto ToDto(SpcControlLimitRevision r) =>
        new(r.RevisionId, r.ParamId, r.RevisionNo, r.ChartType.ToString(), r.CenterLine,
            r.Ucl, r.Lcl, r.EffectiveFrom, r.Reason);
    private static SpcRuleViolationDto ToDto(SpcRuleViolation v) =>
        new(v.ViolationId, v.ParamId, v.LimitRevisionId, v.ObservationId,
            v.RuleCode.ToString(), v.DetectedAt, v.Evidence);
    private static SpcSubgroupEvaluationDto ToDto(SpcSubgroupEvaluation e) =>
        new(e.Subgroup.SubgroupId, e.Subgroup.ParamId,
            e.Subgroup.Observations[0].LimitRevisionId, e.Subgroup.ChartType.ToString(),
            e.Subgroup.ObservedAt, e.Subgroup.Observations.Select(x => x.Value).ToList(),
            e.Violations.Select(ToDto).ToList(), e.IsReplay);
    private static SamplingPlanRevisionDto ToDto(SamplingPlanRevision p) =>
        new(p.PlanRevisionId, p.PlanId, p.RevisionNo, p.Mode.ToString(), p.LotSizeMin,
            p.LotSizeMax, p.SampleSize, p.AcceptanceNumber, p.RejectionNumber,
            p.Aql, p.StandardName, p.StandardVersion, p.EffectiveFrom);
    private static SamplingDecisionDto ToDto(SamplingDecision d) =>
        new(d.Disposition.ToString(), d.RequiredSampleSize, d.InspectedQuantity,
            d.DefectQuantity, d.Reason);
    private static AiModelVersionDto ToDto(AiInspectionModelVersion m) =>
        new(m.ModelVersionId, m.ModelId, m.VersionNo, m.ArtifactUri.ToString(),
            m.ArtifactSha256, m.ConfidenceThreshold, m.EffectiveFrom);
    private static AiInferenceDto ToDto(AiInspectionInference i) =>
        new(i.InferenceId, i.IdempotencyKey, i.ModelVersionId, i.InspectionId,
            i.ImageUri.ToString(), i.ImageSha256, i.RawVerdict.ToString(), i.Confidence,
            i.Threshold, i.InferredAt, i.RequiresReview);
    private static AiReviewDto ToDto(AiInspectionReview r) =>
        new(r.ReviewId, r.InferenceId, r.ReviewSequence, r.ReviewerId,
            r.Verdict.ToString(), r.Reason, r.ReviewedAt);
}
