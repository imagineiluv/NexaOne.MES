using System.Text.RegularExpressions;
using NexaOne.Common;

namespace NexaOne.QMS.Domain;

/// <summary>AI 모델이 반환한 원시 판정.</summary>
public enum AiRawVerdict { Pass, Fail, Unknown }

/// <summary>AI 추론을 확인한 검토자의 최종 판정.</summary>
public enum AiReviewVerdict { Pass, Fail }

/// <summary>아티팩트 SHA 식별자와 판정 임계값이 고정된 AI 모델 리비전.</summary>
public sealed record AiInspectionModelVersion(
    string ModelVersionId,
    string ModelId,
    int VersionNo,
    Uri ArtifactUri,
    string ArtifactSha256,
    decimal ConfidenceThreshold,
    DateTime EffectiveFrom)
{
    /// <summary>모델 리비전 식별자, 아티팩트 SHA-256, 임계값을 검증해 생성한다.</summary>
    public static Result<AiInspectionModelVersion> Create(
        string modelVersionId, string modelId, int versionNo, string artifactUri,
        string artifactSha256, decimal confidenceThreshold, DateTime effectiveFrom)
    {
        if (string.IsNullOrWhiteSpace(modelVersionId) || string.IsNullOrWhiteSpace(modelId))
            return Result.Failure<AiInspectionModelVersion>(Error.Validation(nameof(modelId), "Model and version IDs are required."));
        if (versionNo <= 0)
            return Result.Failure<AiInspectionModelVersion>(Error.Validation(nameof(versionNo), "Version number must be positive."));
        if (!Uri.TryCreate(artifactUri, UriKind.Absolute, out var uri))
            return Result.Failure<AiInspectionModelVersion>(Error.Validation(nameof(artifactUri), "Artifact URI must be absolute."));
        // URI만으로는 파일 교체를 탐지할 수 없으므로 배포 아티팩트의 SHA-256을 리비전에 고정한다.
        if (!Sha256Value.IsValid(artifactSha256))
            return Result.Failure<AiInspectionModelVersion>(Error.Validation(nameof(artifactSha256), "Artifact SHA-256 must be 64 hexadecimal characters."));
        if (confidenceThreshold < 0 || confidenceThreshold > 1)
            return Result.Failure<AiInspectionModelVersion>(Error.Validation(nameof(confidenceThreshold), "Confidence threshold must be between 0 and 1."));
        if (effectiveFrom == default)
            return Result.Failure<AiInspectionModelVersion>(Error.Validation(nameof(effectiveFrom), "Effective time is required."));

        return new AiInspectionModelVersion(modelVersionId, modelId, versionNo, uri,
            artifactSha256.ToLowerInvariant(), confidenceThreshold, effectiveFrom);
    }
}

/// <summary>후속 판독이 수정하지 않는 불변 AI 추론 원본.</summary>
public sealed record AiInspectionInference(
    string InferenceId,
    string IdempotencyKey,
    string ModelVersionId,
    string InspectionId,
    Uri ImageUri,
    string ImageSha256,
    AiRawVerdict RawVerdict,
    decimal Confidence,
    decimal Threshold,
    DateTime InferredAt,
    string RequestHash)
{
    /// <summary>모델 판정이 불확실하거나 신뢰도가 임계값 미만인지 확인한다.</summary>
    public bool RequiresReview => RawVerdict == AiRawVerdict.Unknown || Confidence < Threshold;

    /// <summary>이미지 SHA-256의 표현 형식, 신뢰도 범위, 요청 해시 형식을 검증해 추론 원본을 생성한다.</summary>
    public static Result<AiInspectionInference> Create(
        string inferenceId, string idempotencyKey, string modelVersionId, string inspectionId,
        string imageUri, string imageSha256, AiRawVerdict rawVerdict,
        decimal confidence, decimal threshold, DateTime inferredAt, string requestHash)
    {
        if (string.IsNullOrWhiteSpace(inferenceId) || string.IsNullOrWhiteSpace(idempotencyKey))
            return Result.Failure<AiInspectionInference>(Error.Validation(nameof(inferenceId), "Inference and idempotency IDs are required."));
        if (string.IsNullOrWhiteSpace(modelVersionId) || string.IsNullOrWhiteSpace(inspectionId))
            return Result.Failure<AiInspectionInference>(Error.Validation(nameof(modelVersionId), "Model version and inspection IDs are required."));
        if (!Uri.TryCreate(imageUri, UriKind.Absolute, out var uri))
            return Result.Failure<AiInspectionInference>(Error.Validation(nameof(imageUri), "Image URI must be absolute."));
        // 여기서는 해시 문자열 형식만 검증한다. 이미지 바이트와의 실제 대조는 수집·저장 경계의 책임이다.
        if (!Sha256Value.IsValid(imageSha256))
            return Result.Failure<AiInspectionInference>(Error.Validation(nameof(imageSha256), "Image SHA-256 must be 64 hexadecimal characters."));
        if (confidence < 0 || confidence > 1 || threshold < 0 || threshold > 1)
            return Result.Failure<AiInspectionInference>(Error.Validation(nameof(confidence), "Confidence and threshold must be between 0 and 1."));
        if (inferredAt == default || !Sha256Value.IsValid(requestHash))
            return Result.Failure<AiInspectionInference>(Error.Validation(nameof(requestHash), "Inference time and request SHA-256 are required."));

        return new AiInspectionInference(inferenceId, idempotencyKey, modelVersionId, inspectionId,
            uri, imageSha256.ToLowerInvariant(), rawVerdict, confidence, threshold,
            inferredAt, requestHash.ToLowerInvariant());
    }
}

/// <summary>AI 추론에 누적되는 검토자의 불변 후속 판독.</summary>
public sealed record AiInspectionReview(
    string ReviewId,
    string InferenceId,
    int ReviewSequence,
    string ReviewerId,
    AiReviewVerdict Verdict,
    string Reason,
    DateTime ReviewedAt)
{
    /// <summary>검토 순번, 검토자, 사유, 시각을 검증해 후속 판독을 생성한다.</summary>
    public static Result<AiInspectionReview> Create(
        string reviewId, string inferenceId, int reviewSequence, string reviewerId,
        AiReviewVerdict verdict, string reason, DateTime reviewedAt)
    {
        if (string.IsNullOrWhiteSpace(reviewId) || string.IsNullOrWhiteSpace(inferenceId))
            return Result.Failure<AiInspectionReview>(Error.Validation(nameof(reviewId), "Review and inference IDs are required."));
        if (reviewSequence <= 0 || string.IsNullOrWhiteSpace(reviewerId))
            return Result.Failure<AiInspectionReview>(Error.Validation(nameof(reviewSequence), "Review sequence and reviewer are required."));
        if (string.IsNullOrWhiteSpace(reason) || reviewedAt == default)
            return Result.Failure<AiInspectionReview>(Error.Validation(nameof(reason), "Review reason and time are required."));
        return new AiInspectionReview(reviewId, inferenceId, reviewSequence, reviewerId,
            verdict, reason.Trim(), reviewedAt);
    }
}

internal static partial class Sha256Value
{
    [GeneratedRegex("^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
    /// <summary>값이 64자 16진수 SHA-256 표현인지 확인한다.</summary>
    public static bool IsValid(string? value) => value is not null && Pattern().IsMatch(value);
}
