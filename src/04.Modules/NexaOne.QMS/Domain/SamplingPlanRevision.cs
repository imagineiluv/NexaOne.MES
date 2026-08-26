using NexaOne.Common;

namespace NexaOne.QMS.Domain;

/// <summary>로트 전수검사와 샘플링 검사 방식.</summary>
public enum InspectionSamplingMode { Full, Sampling }

/// <summary>검사 수량과 불량 수량에 따른 샘플링 판정.</summary>
public enum SamplingDisposition { Accept, Reject, Inconclusive }

/// <summary>기존 값을 제자리에서 수정하지 않는 효력 기반 샘플링 계획 리비전.</summary>
public sealed record SamplingPlanRevision(
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
    /// <summary>로트 범위, 샘플 수, Ac/Re, AQL, 표준 정보를 검증해 계획 리비전을 생성한다.</summary>
    public static Result<SamplingPlanRevision> Create(
        string planRevisionId, string planId, int revisionNo, InspectionSamplingMode mode,
        int lotSizeMin, int? lotSizeMax, int? sampleSize,
        int acceptanceNumber, int rejectionNumber, decimal aql,
        string standardName, string standardVersion, DateTime effectiveFrom)
    {
        if (string.IsNullOrWhiteSpace(planRevisionId) || string.IsNullOrWhiteSpace(planId))
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(planId), "Plan and revision IDs are required."));
        if (revisionNo <= 0)
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(revisionNo), "Revision number must be positive."));
        if (lotSizeMin <= 0 || (lotSizeMax.HasValue && lotSizeMax.Value < lotSizeMin))
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(lotSizeMin), "Lot-size range is invalid."));
        if (mode == InspectionSamplingMode.Sampling && (!sampleSize.HasValue || sampleSize.Value <= 0))
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(sampleSize), "Sampling mode requires a positive sample size."));
        if (mode == InspectionSamplingMode.Full && sampleSize.HasValue)
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(sampleSize), "Full inspection derives sample size from lot size."));
        if (acceptanceNumber < 0 || rejectionNumber != acceptanceNumber + 1)
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(rejectionNumber), "Re must equal Ac + 1."));
        if (aql < 0 || aql > 100)
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(aql), "AQL must be between 0 and 100 percent."));
        if (string.IsNullOrWhiteSpace(standardName) || string.IsNullOrWhiteSpace(standardVersion))
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(standardName), "Standard name and version are required."));
        if (effectiveFrom == default)
            return Result.Failure<SamplingPlanRevision>(Error.Validation(nameof(effectiveFrom), "Effective time is required."));

        return new SamplingPlanRevision(planRevisionId, planId, revisionNo, mode,
            lotSizeMin, lotSizeMax, sampleSize, acceptanceNumber, rejectionNumber,
            aql, standardName, standardVersion, effectiveFrom);
    }
}

/// <summary>필요 검사 수량과 불량 수량을 포함한 로트 샘플링 판정.</summary>
public sealed record SamplingDecision(
    SamplingDisposition Disposition,
    int RequiredSampleSize,
    int InspectedQuantity,
    int DefectQuantity,
    string Reason);

/// <summary>전수·샘플링 계획을 로트 검사 실적에 적용한다.</summary>
public static class SamplingPlanCalculator
{
    /// <summary>효력 계획의 필요 수량과 Ac/Re 기준으로 로트 검사 결과를 판정한다.</summary>
    public static Result<SamplingDecision> Evaluate(
        SamplingPlanRevision plan, int lotSize, int inspectedQuantity, int defectQuantity)
    {
        if (lotSize < plan.LotSizeMin || (plan.LotSizeMax.HasValue && lotSize > plan.LotSizeMax.Value))
            return Result.Failure<SamplingDecision>(Error.Validation(nameof(lotSize), "Lot size is outside the plan revision range."));
        if (inspectedQuantity < 0 || inspectedQuantity > lotSize)
            return Result.Failure<SamplingDecision>(Error.Validation(nameof(inspectedQuantity), "Inspected quantity is invalid."));
        if (defectQuantity < 0 || defectQuantity > inspectedQuantity)
            return Result.Failure<SamplingDecision>(Error.Validation(nameof(defectQuantity), "Defect quantity is invalid."));

        // 전수검사는 로트 전체를, 샘플링은 리비전에 고정된 표본 수를 완료해야 판정한다.
        var required = plan.Mode == InspectionSamplingMode.Full ? lotSize : plan.SampleSize!.Value;
        if (required > lotSize)
            return Result.Failure<SamplingDecision>(Error.Validation(nameof(plan.SampleSize), "Sample size cannot exceed lot size."));
        if (inspectedQuantity < required)
            return new SamplingDecision(SamplingDisposition.Inconclusive, required,
                inspectedQuantity, defectQuantity, "Required sample is not complete.");
        if (defectQuantity <= plan.AcceptanceNumber)
            return new SamplingDecision(SamplingDisposition.Accept, required,
                inspectedQuantity, defectQuantity, "Defect quantity is at or below Ac.");
        return new SamplingDecision(SamplingDisposition.Reject, required,
            inspectedQuantity, defectQuantity, "Defect quantity is at or above Re.");
    }
}
