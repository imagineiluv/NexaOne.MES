using NexaOne.Common;
using NexaOne.QMS.Domain;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NexaOne.QMS.Application.Qms;

/// <summary>AI 검사 모델 리비전, 추론 원본, 사람의 후속 판독을 불변 이력으로 조정한다.</summary>
public sealed class AiInspectionService
{
    private readonly IAiInspectionRepository _repository;

    /// <summary>AI 검사 이력 저장소로 서비스를 생성한다.</summary>
    public AiInspectionService(IAiInspectionRepository repository) => _repository = repository;

    /// <summary>아티팩트 SHA 식별자와 판정 임계값을 포함한 AI 모델 리비전을 등록한다.</summary>
    public async Task<Result<AiInspectionModelVersion>> RegisterModelVersionAsync(
        string modelVersionId, string modelId, int versionNo, string artifactUri,
        string artifactSha256, decimal confidenceThreshold, DateTime effectiveFrom,
        CancellationToken ct = default)
    {
        var model = AiInspectionModelVersion.Create(modelVersionId, modelId, versionNo,
            artifactUri, artifactSha256, confidenceThreshold, effectiveFrom);
        if (model.IsFailure) return model;
        await _repository.AddModelVersionAsync(model.Value, ct);
        return model;
    }

    /// <summary>AI 추론 원본을 멱등하게 기록하고 모델 리비전의 임계값을 결과에 고정한다.</summary>
    public async Task<Result<AiInspectionInference>> RecordInferenceAsync(
        string inferenceId, string idempotencyKey, string modelVersionId, string inspectionId,
        string imageUri, string imageSha256, AiRawVerdict rawVerdict,
        decimal confidence, DateTime inferredAt,
        CancellationToken ct = default)
    {
        // 재시도가 같은 멱등 키를 사용해도 실제 이미지·모델·판정이 다르면 충돌로 탐지한다.
        var requestHash = ComputeRequestHash(inferenceId, modelVersionId, inspectionId,
            imageUri, imageSha256, rawVerdict, confidence, inferredAt);
        var existing = await _repository.GetInferenceByIdempotencyKeyAsync(idempotencyKey, ct);
        if (existing is not null)
            return string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase)
                ? Result.Success(existing)
                : Result.Failure<AiInspectionInference>(Error.Conflict("Idempotency key was already used for a different inference request."));

        var model = await _repository.GetModelVersionAsync(modelVersionId, ct);
        if (model is null)
            return Result.Failure<AiInspectionInference>(Error.NotFoundOf(nameof(AiInspectionModelVersion), modelVersionId));
        if (model.EffectiveFrom.ToUniversalTime() > inferredAt.ToUniversalTime())
            return Result.Failure<AiInspectionInference>(Error.Validation(
                nameof(modelVersionId), "The AI model version is not effective at the inference time."));
        if (!await _repository.InspectionExistsAsync(inspectionId, ct))
            return Result.Failure<AiInspectionInference>(Error.NotFoundOf("Inspection", inspectionId));

        var inference = AiInspectionInference.Create(inferenceId, idempotencyKey,
            modelVersionId, inspectionId, imageUri, imageSha256, rawVerdict,
            confidence, model.ConfidenceThreshold, inferredAt, requestHash);
        if (inference.IsFailure) return inference;
        try
        {
            await _repository.AddInferenceAsync(inference.Value, ct);
            return inference;
        }
        catch
        {
            // 동시 요청은 둘 다 최초 조회를 통과할 수 있다. DB 고유 키를 최종 기준으로 삼아
            // 재조회하고, 요청 해시까지 일치할 때만 이미 저장된 승자를 반환한다.
            existing = await _repository.GetInferenceByIdempotencyKeyAsync(idempotencyKey, ct);
            if (existing is null) throw;
            return string.Equals(existing.RequestHash, requestHash, StringComparison.OrdinalIgnoreCase)
                ? Result.Success(existing)
                : Result.Failure<AiInspectionInference>(Error.Conflict("Idempotency key was concurrently used for a different inference request."));
        }
    }

    /// <summary>저장된 AI 추론에 대한 사람의 후속 판독을 새 순번으로 추가한다.</summary>
    public async Task<Result<AiInspectionReview>> ReviewAsync(
        string reviewId, string inferenceId, string reviewerId,
        AiReviewVerdict verdict, string reason, DateTime reviewedAt,
        CancellationToken ct = default)
    {
        if (await _repository.GetInferenceAsync(inferenceId, ct) is null)
            return Result.Failure<AiInspectionReview>(Error.NotFoundOf(nameof(AiInspectionInference), inferenceId));
        const int maxAttempts = 5;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var reviews = await _repository.GetReviewsAsync(inferenceId, ct);
            var sequence = reviews.Count == 0 ? 1 : reviews.Max(x => x.ReviewSequence) + 1;
            var review = AiInspectionReview.Create(reviewId, inferenceId, sequence,
                reviewerId, verdict, reason, reviewedAt);
            if (review.IsFailure) return review;

            try
            {
                await _repository.AddReviewAsync(review.Value, ct);
                return review;
            }
            catch (Exception) when (!ct.IsCancellationRequested && attempt < maxAttempts)
            {
                // A concurrent reviewer may have allocated the same sequence. If this request
                // already won by review ID, replay it; otherwise recompute Max+1 and append again.
                var winner = await _repository.GetReviewAsync(reviewId, ct);
                if (winner is not null)
                    return SameReviewRequest(
                            winner, inferenceId, reviewerId, verdict, reason, reviewedAt)
                        ? Result.Success(winner)
                        : Result.Failure<AiInspectionReview>(Error.Conflict(
                            "Review ID was concurrently used for a different review request."));
            }
        }

        // The loop returns on success; this guard only satisfies definite assignment if the
        // repository repeatedly rejects non-cancellation writes on the final attempt.
        throw new InvalidOperationException("Unable to allocate an AI review sequence after repeated conflicts.");
    }

    private static bool SameReviewRequest(
        AiInspectionReview existing,
        string inferenceId,
        string reviewerId,
        AiReviewVerdict verdict,
        string reason,
        DateTime reviewedAt)
        => string.Equals(existing.InferenceId, inferenceId, StringComparison.Ordinal)
           && string.Equals(existing.ReviewerId, reviewerId, StringComparison.Ordinal)
           && existing.Verdict == verdict
           && string.Equals(existing.Reason, reason, StringComparison.Ordinal)
           && existing.ReviewedAt.ToUniversalTime() == reviewedAt.ToUniversalTime();

    /// <summary>식별자로 AI 추론 원본을 조회한다.</summary>
    public async Task<Result<AiInspectionInference>> GetInferenceAsync(
        string inferenceId, CancellationToken ct = default)
    {
        var inference = await _repository.GetInferenceAsync(inferenceId, ct);
        return inference is null
            ? Result.Failure<AiInspectionInference>(Error.NotFoundOf(nameof(AiInspectionInference), inferenceId))
            : Result.Success(inference);
    }

    /// <summary>AI 추론에 누적된 후속 판독을 순서대로 조회한다.</summary>
    public Task<IReadOnlyList<AiInspectionReview>> GetReviewsAsync(
        string inferenceId, CancellationToken ct = default)
        => _repository.GetReviewsAsync(inferenceId, ct);

    /// <summary>검사 실행 상세에 합칠 AI 이미지/모델 추론 증적을 조회합니다.</summary>
    public Task<IReadOnlyList<AiInspectionInference>> GetInferencesByInspectionAsync(
        string inspectionId, CancellationToken ct = default)
        => _repository.GetInferencesByInspectionAsync(inspectionId, ct);

    private static string ComputeRequestHash(
        string inferenceId, string modelVersionId, string inspectionId,
        string imageUri, string imageSha256, AiRawVerdict verdict,
        decimal confidence, DateTime inferredAt)
    {
        // URI 원문은 요청 의미의 일부로 보존하고, SHA 대소문자와 수치 표현만 정규화한다.
        // 시각은 DateTime.Kind 규칙에 따라 UTC 표기로 직렬화하므로 호출자는 의미가 명확한 UTC 값을 전달해야 한다.
        var canonical = string.Join("\n", inferenceId, modelVersionId, inspectionId,
            imageUri, imageSha256.ToLowerInvariant(), verdict.ToString(),
            confidence.ToString(CultureInfo.InvariantCulture),
            inferredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
