using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NexaOne.Common;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

/// <summary>SPC 부분군 평가 결과와 재실행 여부를 함께 전달한다.</summary>
public sealed record SpcSubgroupEvaluation(
    SpcSubgroup Subgroup, IReadOnlyList<SpcRuleViolation> Violations, bool IsReplay);

/// <summary>SPC 관리한계 리비전, 부분군 규칙 평가, 로트 샘플링 판정을 조정한다.</summary>
public sealed class AdvancedQualityService
{
    private readonly IAdvancedQualityRepository _repository;

    /// <summary>고급 품질 데이터를 저장할 저장소로 서비스를 생성한다.</summary>
    public AdvancedQualityService(IAdvancedQualityRepository repository) => _repository = repository;

    /// <summary>검증된 SPC 관리한계를 새 효력 리비전으로 추가한다.</summary>
    public async Task<Result<SpcControlLimitRevision>> AddLimitRevisionAsync(
        string revisionId, string paramId, int revisionNo, SpcControlChartType chartType,
        decimal centerLine, decimal ucl, decimal lcl, DateTime effectiveFrom, string reason,
        CancellationToken ct = default)
    {
        var result = SpcControlLimitRevision.Create(revisionId, paramId, revisionNo,
            chartType, centerLine, ucl, lcl, effectiveFrom, reason);
        if (result.IsFailure) return result;
        await _repository.AddLimitRevisionAsync(result.Value, ct);
        return result;
    }

    /// <summary>부분군 관측값에 SPC 신호 규칙을 적용하고, 이미 저장된 같은 멱등 요청은 재생한다.</summary>
    public async Task<Result<SpcSubgroupEvaluation>> EvaluateSubgroupAsync(
        string subgroupId, string idempotencyKey, string limitRevisionId,
        DateTime observedAt, IReadOnlyList<decimal> values, string sourceType, string actorId,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(subgroupId) || string.IsNullOrWhiteSpace(idempotencyKey))
            return Result.Failure<SpcSubgroupEvaluation>(Error.Validation(nameof(subgroupId), "Subgroup and idempotency IDs are required."));
        if (values is null || values.Count == 0)
            return Result.Failure<SpcSubgroupEvaluation>(Error.Validation(nameof(values), "At least one observation is required."));
        if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(actorId) || observedAt == default)
            return Result.Failure<SpcSubgroupEvaluation>(Error.Validation(nameof(sourceType), "Source, actor, and observation time are required."));

        // 멱등 키만 같고 실제 관측 요청이 다른 오용을 구분하기 위해 논리 입력 전체를 해시한다.
        var requestHash = ComputeHash(subgroupId, limitRevisionId, observedAt, values, sourceType);
        var replay = await _repository.GetSubgroupByIdempotencyKeyAsync(idempotencyKey, ct);
        if (replay is not null)
        {
            if (!string.Equals(replay.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase))
                return Result.Failure<SpcSubgroupEvaluation>(Error.Conflict("SPC idempotency key was used for a different subgroup request."));
            var revision = await _repository.GetLimitRevisionAsync(limitRevisionId, ct);
            if (revision is null)
                return Result.Failure<SpcSubgroupEvaluation>(Error.NotFoundOf(nameof(SpcControlLimitRevision), limitRevisionId));
            // 해시가 일치하므로 현재 요청 값으로 부분군을 복원하고, 저장된 위반 결과는 재생성하지 않는다.
            var restored = BuildSubgroup(replay.SubgroupId, revision, observedAt, values);
            var prior = await _repository.GetViolationsAsync(revision.ParamId, replay.SubgroupId, ct);
            return new SpcSubgroupEvaluation(restored, prior, true);
        }

        var limits = await _repository.GetLimitRevisionAsync(limitRevisionId, ct);
        if (limits is null)
            return Result.Failure<SpcSubgroupEvaluation>(Error.NotFoundOf(nameof(SpcControlLimitRevision), limitRevisionId));
        var subgroup = BuildSubgroup(subgroupId, limits, observedAt, values);
        var violations = SpcRuleEngine.Evaluate(limits, subgroup.Observations);
        // 선조회 이후 동시에 들어온 최초 INSERT 경합은 이 서비스가 재생하지 않고 저장소 예외로 전파한다.
        await _repository.AddSubgroupEvaluationAsync(subgroup, idempotencyKey, requestHash,
            sourceType, actorId, violations, ct);
        return new SpcSubgroupEvaluation(subgroup, violations, false);
    }

    /// <summary>파라미터 또는 부분군 조건에 해당하는 SPC 규칙 위반을 조회한다.</summary>
    public Task<IReadOnlyList<SpcRuleViolation>> GetViolationsAsync(
        string? paramId, string? subgroupId, CancellationToken ct = default)
        => _repository.GetViolationsAsync(paramId, subgroupId, ct);

    /// <summary>검증된 전수·샘플링 검사 계획을 새 효력 리비전으로 추가한다.</summary>
    public async Task<Result<SamplingPlanRevision>> AddSamplingPlanRevisionAsync(
        string planRevisionId, string planId, int revisionNo, InspectionSamplingMode mode,
        int lotSizeMin, int? lotSizeMax, int? sampleSize, int acceptanceNumber,
        int rejectionNumber, decimal aql, string standardName, string standardVersion,
        DateTime effectiveFrom, CancellationToken ct = default)
    {
        var result = SamplingPlanRevision.Create(planRevisionId, planId, revisionNo, mode,
            lotSizeMin, lotSizeMax, sampleSize, acceptanceNumber, rejectionNumber, aql,
            standardName, standardVersion, effectiveFrom);
        if (result.IsFailure) return result;
        await _repository.AddSamplingPlanRevisionAsync(result.Value, ct);
        return result;
    }

    /// <summary>로트 크기와 효력 시점에 맞는 검사 계획으로 합격·불합격·미완료를 판정한다.</summary>
    public async Task<Result<SamplingDecision>> EvaluateSamplingAsync(
        int lotSize, int inspectedQuantity, int defectQuantity, DateTime effectiveAt,
        CancellationToken ct = default)
    {
        var plan = await _repository.SelectSamplingPlanAsync(lotSize, effectiveAt, ct);
        return plan is null
            ? Result.Failure<SamplingDecision>(Error.NotFoundOf(nameof(SamplingPlanRevision), lotSize.ToString(CultureInfo.InvariantCulture)))
            : SamplingPlanCalculator.Evaluate(plan, lotSize, inspectedQuantity, defectQuantity);
    }

    /// <summary>로트 크기와 효력 시점을 만족하는 최신 샘플링 계획 리비전을 조회한다.</summary>
    public Task<SamplingPlanRevision?> SelectSamplingPlanAsync(int lotSize, DateTime effectiveAt, CancellationToken ct = default)
        => _repository.SelectSamplingPlanAsync(lotSize, effectiveAt, ct);

    private static SpcSubgroup BuildSubgroup(
        string subgroupId, SpcControlLimitRevision limits, DateTime observedAt, IReadOnlyList<decimal> values)
    {
        // 입력 순서로 결정적 관측 ID와 최소 틱 간격을 부여해 동시 시각의 표본도 안정적으로 정렬한다.
        var observations = values.Select((value, index) => new SpcObservation(
            $"{subgroupId}:{index + 1}", limits.ParamId, limits.RevisionId, subgroupId,
            index + 1, value, observedAt.AddTicks(index))).ToList();
        return new SpcSubgroup(subgroupId, limits.ParamId, limits.ChartType, observedAt, observations);
    }

    private static string ComputeHash(
        string subgroupId, string revisionId, DateTime observedAt,
        IReadOnlyList<decimal> values, string sourceType)
    {
        // 수치는 문화권 독립 형식으로, 시각은 DateTime.Kind 규칙에 따른 UTC 표기로 직렬화해 요청 해시를 만든다.
        var canonical = string.Join("\n", subgroupId, revisionId,
            observedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture), sourceType,
            string.Join(",", values.Select(x => x.ToString(CultureInfo.InvariantCulture))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
