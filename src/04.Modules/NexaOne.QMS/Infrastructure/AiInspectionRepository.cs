using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

/// <summary>AI 모델 리비전, 추론 원본, 후속 판독을 추가 전용 이력으로 저장한다.</summary>
public sealed class AiInspectionRepository : QueryRepository, IAiInspectionRepository
{
    private readonly ServiceObjectProcessor _processor;

    /// <summary>QMS 데이터 소스로 AI 검사 저장소를 생성한다.</summary>
    public AiInspectionRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    /// <summary>AI 모델 리비전을 조회한다.</summary>
    public async Task<AiInspectionModelVersion?> GetModelVersionAsync(string modelVersionId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<ModelRow>(
            "SELECT * FROM QMS_AI_MODEL_VERSION WHERE MODEL_VERSION_ID = @modelVersionId",
            new { modelVersionId }, ct))?.ToDomain();

    /// <summary>AI 모델 리비전을 추가한다.</summary>
    public Task AddModelVersionAsync(AiInspectionModelVersion model, CancellationToken ct = default)
        => ExecuteInsertAsync(@"INSERT INTO QMS_AI_MODEL_VERSION
            (MODEL_VERSION_ID, MODEL_ID, VERSION_NO, ARTIFACT_URI, ARTIFACT_SHA256,
             CONFIDENCE_THRESHOLD, EFFECTIVE_FROM, CREATED_BY, CREATED_AT)
            VALUES (@ModelVersionId, @ModelId, @VersionNo, @ArtifactUri, @ArtifactSha256,
             @ConfidenceThreshold, @EffectiveFrom, @CreatedBy, @CreatedAt)", new
        {
            model.ModelVersionId,
            model.ModelId,
            model.VersionNo,
            ArtifactUri = model.ArtifactUri.ToString(),
            model.ArtifactSha256,
            model.ConfidenceThreshold,
            model.EffectiveFrom
        }, ct);

    /// <summary>AI 추론 원본을 식별자로 조회한다.</summary>
    public async Task<AiInspectionInference?> GetInferenceAsync(string inferenceId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<InferenceRow>(
            "SELECT * FROM QMS_AI_INFERENCE WHERE INFERENCE_ID = @inferenceId",
            new { inferenceId }, ct))?.ToDomain();

    /// <summary>AI 추론 원본을 멱등 키로 조회한다.</summary>
    public async Task<AiInspectionInference?> GetInferenceByIdempotencyKeyAsync(string idempotencyKey, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<InferenceRow>(
            "SELECT * FROM QMS_AI_INFERENCE WHERE IDEMPOTENCY_KEY = @idempotencyKey",
            new { idempotencyKey }, ct))?.ToDomain();

    public async Task<bool> InspectionExistsAsync(
        string inspectionId, CancellationToken ct = default)
        => await QueryFirstOrDefaultAsync<CountRow>(
            "SELECT COUNT(*) AS ROW_COUNT FROM QMS_INSPECTION WHERE INSPECTION_ID = @inspectionId",
            new { inspectionId }, ct) is { RowCount: > 0 };

    public async Task<IReadOnlyList<AiInspectionInference>> GetInferencesByInspectionAsync(
        string inspectionId, CancellationToken ct = default)
    {
        var rows = await QueryAsync<InferenceRow>(@"SELECT * FROM QMS_AI_INFERENCE
            WHERE INSPECTION_ID = @inspectionId
            ORDER BY INFERRED_AT, INFERENCE_ID", new { inspectionId }, ct);
        return rows.Select(x => x.ToDomain()).ToArray();
    }

    /// <summary>이미지·요청 SHA-256과 함께 AI 추론 원본을 추가한다.</summary>
    public Task AddInferenceAsync(AiInspectionInference inference, CancellationToken ct = default)
        => ExecuteInsertAsync(@"INSERT INTO QMS_AI_INFERENCE
            (INFERENCE_ID, IDEMPOTENCY_KEY, MODEL_VERSION_ID, INSPECTION_ID,
             IMAGE_URI, IMAGE_SHA256, RAW_VERDICT, CONFIDENCE, THRESHOLD,
             INFERRED_AT, REQUEST_HASH, CREATED_BY, CREATED_AT)
            VALUES (@InferenceId, @IdempotencyKey, @ModelVersionId, @InspectionId,
             @ImageUri, @ImageSha256, @RawVerdict, @Confidence, @Threshold,
             @InferredAt, @RequestHash, @CreatedBy, @CreatedAt)", new
        {
            inference.InferenceId,
            inference.IdempotencyKey,
            inference.ModelVersionId,
            inference.InspectionId,
            ImageUri = inference.ImageUri.ToString(),
            inference.ImageSha256,
            RawVerdict = inference.RawVerdict.ToString(),
            inference.Confidence,
            inference.Threshold,
            inference.InferredAt,
            inference.RequestHash
        }, ct);

    /// <summary>추론에 연결된 후속 판독을 순서대로 조회한다.</summary>
    public async Task<IReadOnlyList<AiInspectionReview>> GetReviewsAsync(string inferenceId, CancellationToken ct = default)
    {
        var rows = await QueryAsync<ReviewRow>(
            "SELECT * FROM QMS_AI_REVIEW WHERE INFERENCE_ID = @inferenceId ORDER BY REVIEW_SEQUENCE",
            new { inferenceId }, ct);
        return rows.Select(x => x.ToDomain()).ToList();
    }

    public async Task<AiInspectionReview?> GetReviewAsync(
        string reviewId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<ReviewRow>(
            "SELECT * FROM QMS_AI_REVIEW WHERE REVIEW_ID = @reviewId",
            new { reviewId }, ct))?.ToDomain();

    /// <summary>사람의 후속 판독을 추가한다.</summary>
    public Task AddReviewAsync(AiInspectionReview review, CancellationToken ct = default)
        => ExecuteInsertAsync(@"INSERT INTO QMS_AI_REVIEW
            (REVIEW_ID, INFERENCE_ID, REVIEW_SEQUENCE, REVIEWER_ID,
             REVIEW_VERDICT, REASON, REVIEWED_AT, CREATED_BY, CREATED_AT)
            VALUES (@ReviewId, @InferenceId, @ReviewSequence, @ReviewerId,
             @ReviewVerdict, @Reason, @ReviewedAt, @CreatedBy, @CreatedAt)", new
        {
            review.ReviewId,
            review.InferenceId,
            review.ReviewSequence,
            review.ReviewerId,
            ReviewVerdict = review.Verdict.ToString(),
            review.Reason,
            review.ReviewedAt
        }, ct);

    private async Task ExecuteInsertAsync(string sql, object param, CancellationToken ct)
        => _ = await _processor.InsertAsync(sql, param, ct);

    private sealed class ModelRow
    {
        public string ModelVersionId { get; set; } = "";
        public string ModelId { get; set; } = "";
        public int VersionNo { get; set; }
        public string ArtifactUri { get; set; } = "";
        public string ArtifactSha256 { get; set; } = "";
        public decimal ConfidenceThreshold { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public AiInspectionModelVersion ToDomain() => new(ModelVersionId, ModelId, VersionNo,
            new Uri(ArtifactUri), ArtifactSha256, ConfidenceThreshold, EffectiveFrom);
    }

    private sealed class CountRow
    {
        public int RowCount { get; set; }
    }

    private sealed class InferenceRow
    {
        public string InferenceId { get; set; } = "";
        public string IdempotencyKey { get; set; } = "";
        public string ModelVersionId { get; set; } = "";
        public string InspectionId { get; set; } = "";
        public string ImageUri { get; set; } = "";
        public string ImageSha256 { get; set; } = "";
        public string RawVerdict { get; set; } = "";
        public decimal Confidence { get; set; }
        public decimal Threshold { get; set; }
        public DateTime InferredAt { get; set; }
        public string RequestHash { get; set; } = "";
        public AiInspectionInference ToDomain() => new(InferenceId, IdempotencyKey,
            ModelVersionId, InspectionId, new Uri(ImageUri), ImageSha256,
            Enum.Parse<AiRawVerdict>(RawVerdict), Confidence, Threshold, InferredAt, RequestHash);
    }

    private sealed class ReviewRow
    {
        public string ReviewId { get; set; } = "";
        public string InferenceId { get; set; } = "";
        public int ReviewSequence { get; set; }
        public string ReviewerId { get; set; } = "";
        public string ReviewVerdict { get; set; } = "";
        public string Reason { get; set; } = "";
        public DateTime ReviewedAt { get; set; }
        public AiInspectionReview ToDomain() => new(ReviewId, InferenceId, ReviewSequence,
            ReviewerId, Enum.Parse<AiReviewVerdict>(ReviewVerdict), Reason, ReviewedAt);
    }
}
