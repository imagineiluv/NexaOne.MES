using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Application.Qms;

/// <summary>AI 검사 모델, 추론 원본, 사람의 후속 판독을 저장하는 경계.</summary>
public interface IAiInspectionRepository
{
    /// <summary>AI 모델 리비전을 조회한다.</summary>
    Task<AiInspectionModelVersion?> GetModelVersionAsync(string modelVersionId, CancellationToken ct = default);

    /// <summary>AI 모델 리비전을 추가한다.</summary>
    Task AddModelVersionAsync(AiInspectionModelVersion model, CancellationToken ct = default);

    /// <summary>AI 추론 원본을 식별자로 조회한다.</summary>
    Task<AiInspectionInference?> GetInferenceAsync(string inferenceId, CancellationToken ct = default);

    /// <summary>AI 추론 원본을 멱등 키로 조회한다.</summary>
    Task<AiInspectionInference?> GetInferenceByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default);

    /// <summary>Checks the inspection link explicitly for providers that run with foreign keys disabled.</summary>
    Task<bool> InspectionExistsAsync(string inspectionId, CancellationToken ct = default);

    /// <summary>검사 실행에 연결된 이미지/모델 추론 증적을 시간순으로 조회합니다.</summary>
    Task<IReadOnlyList<AiInspectionInference>> GetInferencesByInspectionAsync(
        string inspectionId, CancellationToken ct = default);

    /// <summary>AI 추론 원본을 추가한다.</summary>
    Task AddInferenceAsync(AiInspectionInference inference, CancellationToken ct = default);

    /// <summary>추론에 연결된 후속 판독을 순서대로 조회한다.</summary>
    Task<IReadOnlyList<AiInspectionReview>> GetReviewsAsync(string inferenceId, CancellationToken ct = default);

    /// <summary>Reads a review by its server contract identifier for unique-race recovery.</summary>
    Task<AiInspectionReview?> GetReviewAsync(string reviewId, CancellationToken ct = default);

    /// <summary>사람의 후속 판독을 추가한다.</summary>
    Task AddReviewAsync(AiInspectionReview review, CancellationToken ct = default);
}
