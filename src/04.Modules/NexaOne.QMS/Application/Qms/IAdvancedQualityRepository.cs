using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

/// <summary>멱등 키로 이미 저장된 SPC 부분군을 재생할 때 필요한 최소 정보.</summary>
public sealed record SpcSubgroupReplay(string SubgroupId, string RequestHash);

/// <summary>SPC 평가와 샘플링 계획의 불변 리비전·실행 이력 저장 경계.</summary>
public interface IAdvancedQualityRepository
{
    /// <summary>식별자로 SPC 관리한계 리비전을 조회한다.</summary>
    Task<SpcControlLimitRevision?> GetLimitRevisionAsync(string revisionId, CancellationToken ct = default);

    /// <summary>SPC 관리한계 리비전을 추가한다.</summary>
    Task AddLimitRevisionAsync(SpcControlLimitRevision revision, CancellationToken ct = default);

    /// <summary>멱등 키에 해당하는 기존 부분군과 요청 해시를 조회한다.</summary>
    Task<SpcSubgroupReplay?> GetSubgroupByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>부분군, 관측값, 규칙 위반을 하나의 평가 단위로 추가한다.</summary>
    Task AddSubgroupEvaluationAsync(SpcSubgroup subgroup, string idempotencyKey, string requestHash,
        string sourceType, string actorId, IReadOnlyList<SpcRuleViolation> violations, CancellationToken ct = default);

    /// <summary>선택 조건에 해당하는 SPC 규칙 위반을 조회한다.</summary>
    Task<IReadOnlyList<SpcRuleViolation>> GetViolationsAsync(string? paramId, string? subgroupId, CancellationToken ct = default);

    /// <summary>샘플링 계획 리비전을 추가한다.</summary>
    Task AddSamplingPlanRevisionAsync(SamplingPlanRevision plan, CancellationToken ct = default);

    /// <summary>로트 크기와 효력 시점에 맞는 샘플링 계획을 선택한다.</summary>
    Task<SamplingPlanRevision?> SelectSamplingPlanAsync(int lotSize, DateTime effectiveAt, CancellationToken ct = default);
}
