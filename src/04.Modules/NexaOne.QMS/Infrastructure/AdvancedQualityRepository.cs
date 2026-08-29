using NexaOne.Infrastructure.Persistence;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.QMS.Infrastructure;

/// <summary>SPC·샘플링 감사 이력을 제자리 수정·삭제 없이 추가만 하는 저장소.</summary>
public sealed class AdvancedQualityRepository : QueryRepository, IAdvancedQualityRepository
{
    private readonly ServiceObjectProcessor _processor;

    /// <summary>QMS 데이터 소스로 고급 품질 저장소를 생성한다.</summary>
    public AdvancedQualityRepository(EesDataSource dataSource) : base(dataSource)
        => _processor = new ServiceObjectProcessor(dataSource);

    /// <summary>SPC 관리한계 리비전을 조회한다.</summary>
    public async Task<SpcControlLimitRevision?> GetLimitRevisionAsync(string revisionId, CancellationToken ct = default)
        => (await QueryFirstOrDefaultAsync<LimitRow>(
            "SELECT * FROM QMS_SPC_LIMIT_REVISION WHERE LIMIT_REVISION_ID = @revisionId",
            new { revisionId }, ct))?.ToDomain();

    /// <summary>SPC 관리한계 리비전을 추가한다.</summary>
    public async Task AddLimitRevisionAsync(SpcControlLimitRevision revision, CancellationToken ct = default)
        => _ = await _processor.InsertAsync(@"INSERT INTO QMS_SPC_LIMIT_REVISION
            (LIMIT_REVISION_ID, PARAM_ID, REVISION_NO, CHART_TYPE, CENTER_LINE, UCL, LCL,
             EFFECTIVE_FROM, REASON, CREATED_BY, CREATED_AT)
            VALUES (@RevisionId, @ParamId, @RevisionNo, @ChartType, @CenterLine, @Ucl, @Lcl,
             @EffectiveFrom, @Reason, @CreatedBy, @CreatedAt)", new
        {
            revision.RevisionId,
            revision.ParamId,
            revision.RevisionNo,
            ChartType = revision.ChartType.ToString(),
            revision.CenterLine,
            revision.Ucl,
            revision.Lcl,
            revision.EffectiveFrom,
            revision.Reason
        }, ct);

    /// <summary>멱등 키로 기존 SPC 부분군과 요청 해시를 조회한다.</summary>
    public async Task<SpcSubgroupReplay?> GetSubgroupByIdempotencyKeyAsync(
        string idempotencyKey, CancellationToken ct = default)
        => await QueryFirstOrDefaultAsync<SpcSubgroupReplay>(@"SELECT SUBGROUP_ID, REQUEST_HASH
            FROM QMS_SPC_SUBGROUP WHERE IDEMPOTENCY_KEY = @idempotencyKey", new { idempotencyKey }, ct);

    /// <summary>SPC 부분군, 관측값, 규칙 위반을 하나의 평가 단위로 추가한다.</summary>
    public async Task AddSubgroupEvaluationAsync(
        SpcSubgroup subgroup, string idempotencyKey, string requestHash,
        string sourceType, string actorId, IReadOnlyList<SpcRuleViolation> violations,
        CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var revisionId = subgroup.Observations[0].LimitRevisionId;
        var statements = new List<(string Sql, object? Param)>
        {
            (@"INSERT INTO QMS_SPC_SUBGROUP
                (SUBGROUP_ID, PARAM_ID, LIMIT_REVISION_ID, IDEMPOTENCY_KEY, REQUEST_HASH,
                 CHART_TYPE, OBSERVED_AT, SAMPLE_COUNT, SUBGROUP_MEAN, SUBGROUP_RANGE,
                 CREATED_BY, CREATED_AT)
                VALUES (@SubgroupId, @ParamId, @LimitRevisionId, @IdempotencyKey, @RequestHash,
                 @ChartType, @ObservedAt, @SampleCount, @SubgroupMean, @SubgroupRange,
                 @CreatedBy, @CreatedAt)", new
            {
                subgroup.SubgroupId, subgroup.ParamId, LimitRevisionId = revisionId,
                IdempotencyKey = idempotencyKey, RequestHash = requestHash,
                ChartType = subgroup.ChartType.ToString(), subgroup.ObservedAt,
                SampleCount = subgroup.Observations.Count, SubgroupMean = subgroup.Mean,
                SubgroupRange = subgroup.Range, CreatedBy = actorId, CreatedAt = now
            })
        };
        statements.AddRange(subgroup.Observations.Select((observation, index) => ((string Sql, object? Param))(
            @"INSERT INTO QMS_SPC_OBSERVATION
                (OBSERVATION_ID, PARAM_ID, LIMIT_REVISION_ID, SUBGROUP_ID, SAMPLE_INDEX,
                 OBSERVED_VALUE, OBSERVED_AT, SOURCE_TYPE, IDEMPOTENCY_KEY, CREATED_BY, CREATED_AT)
                VALUES (@ObservationId, @ParamId, @LimitRevisionId, @SubgroupId, @SampleIndex,
                 @Value, @ObservedAt, @SourceType, @IdempotencyKey, @CreatedBy, @CreatedAt)", new
            {
                observation.ObservationId,
                observation.ParamId,
                observation.LimitRevisionId,
                observation.SubgroupId,
                observation.SampleIndex,
                observation.Value,
                observation.ObservedAt,
                SourceType = sourceType,
                IdempotencyKey = $"{idempotencyKey}:{index + 1}",
                CreatedBy = actorId,
                CreatedAt = now
            })));
        statements.AddRange(violations.Select(violation => ((string Sql, object? Param))(
            @"INSERT INTO QMS_SPC_RULE_VIOLATION
                (VIOLATION_ID, PARAM_ID, LIMIT_REVISION_ID, OBSERVATION_ID, RULE_CODE,
                 DETECTED_AT, EVIDENCE, CREATED_BY, CREATED_AT)
                VALUES (@ViolationId, @ParamId, @LimitRevisionId, @ObservationId, @RuleCode,
                 @DetectedAt, @Evidence, @CreatedBy, @CreatedAt)", new
            {
                violation.ViolationId,
                violation.ParamId,
                violation.LimitRevisionId,
                violation.ObservationId,
                RuleCode = violation.RuleCode.ToString(),
                violation.DetectedAt,
                violation.Evidence,
                CreatedBy = actorId,
                CreatedAt = now
            })));
        // 부분군·관측값·위반은 하나의 SPC 결과이므로 한 트랜잭션에서 전체 커밋한다.
        await _processor.ExecuteManyAsync(ct, statements.ToArray());
    }

    /// <summary>파라미터·부분군 조건으로 SPC 규칙 위반을 조회한다.</summary>
    public async Task<IReadOnlyList<SpcRuleViolation>> GetViolationsAsync(
        string? paramId, string? subgroupId, CancellationToken ct = default)
    {
        var rows = await QueryAsync<ViolationRow>(@"SELECT V.*
            FROM QMS_SPC_RULE_VIOLATION V
            JOIN QMS_SPC_OBSERVATION O ON O.OBSERVATION_ID = V.OBSERVATION_ID
            WHERE (@paramId IS NULL OR V.PARAM_ID = @paramId)
              AND (@subgroupId IS NULL OR O.SUBGROUP_ID = @subgroupId)
            ORDER BY V.DETECTED_AT DESC", new { paramId, subgroupId }, ct);
        return rows.Select(x => x.ToDomain()).ToList();
    }

    /// <summary>샘플링 계획 리비전을 추가한다.</summary>
    public async Task AddSamplingPlanRevisionAsync(SamplingPlanRevision plan, CancellationToken ct = default)
        => _ = await _processor.InsertAsync(@"INSERT INTO QMS_SAMPLING_PLAN_REVISION
            (PLAN_REVISION_ID, PLAN_ID, REVISION_NO, INSPECTION_MODE, LOT_SIZE_MIN,
             LOT_SIZE_MAX, SAMPLE_SIZE, ACCEPTANCE_NO, REJECTION_NO, AQL,
             STANDARD_NAME, STANDARD_VERSION, EFFECTIVE_FROM, CREATED_BY, CREATED_AT)
            VALUES (@PlanRevisionId, @PlanId, @RevisionNo, @Mode, @LotSizeMin,
             @LotSizeMax, @SampleSize, @AcceptanceNumber, @RejectionNumber, @Aql,
             @StandardName, @StandardVersion, @EffectiveFrom, @CreatedBy, @CreatedAt)", new
        {
            plan.PlanRevisionId,
            plan.PlanId,
            plan.RevisionNo,
            Mode = plan.Mode.ToString(),
            plan.LotSizeMin,
            plan.LotSizeMax,
            plan.SampleSize,
            plan.AcceptanceNumber,
            plan.RejectionNumber,
            plan.Aql,
            plan.StandardName,
            plan.StandardVersion,
            plan.EffectiveFrom
        }, ct);

    /// <summary>로트 크기와 효력 시점을 만족하는 최신 샘플링 계획을 선택한다.</summary>
    public async Task<SamplingPlanRevision?> SelectSamplingPlanAsync(
        int lotSize, DateTime effectiveAt, CancellationToken ct = default)
        // 동일 효력 범위가 겹치면 가장 최근 적용 시점과 높은 리비전을 우선한다.
        => (await QueryFirstOrDefaultAsync<SamplingRow>(@"SELECT * FROM QMS_SAMPLING_PLAN_REVISION
            WHERE LOT_SIZE_MIN <= @lotSize AND (LOT_SIZE_MAX IS NULL OR LOT_SIZE_MAX >= @lotSize)
              AND EFFECTIVE_FROM <= @effectiveAt
            ORDER BY EFFECTIVE_FROM DESC, REVISION_NO DESC", new { lotSize, effectiveAt }, ct))?.ToDomain();

    private sealed class LimitRow
    {
        public string LimitRevisionId { get; set; } = "";
        public string ParamId { get; set; } = "";
        public int RevisionNo { get; set; }
        public string ChartType { get; set; } = "";
        public decimal CenterLine { get; set; }
        public decimal Ucl { get; set; }
        public decimal Lcl { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public string Reason { get; set; } = "";
        public SpcControlLimitRevision ToDomain() => new(LimitRevisionId, ParamId, RevisionNo,
            Enum.Parse<SpcControlChartType>(ChartType), CenterLine, Ucl, Lcl, EffectiveFrom, Reason);
    }

    private sealed class ViolationRow
    {
        public string ViolationId { get; set; } = "";
        public string ParamId { get; set; } = "";
        public string LimitRevisionId { get; set; } = "";
        public string ObservationId { get; set; } = "";
        public string RuleCode { get; set; } = "";
        public DateTime DetectedAt { get; set; }
        public string Evidence { get; set; } = "";
        public SpcRuleViolation ToDomain() => new(ViolationId, ParamId, LimitRevisionId,
            ObservationId, Enum.Parse<SpcRuleCode>(RuleCode), DetectedAt, Evidence);
    }

    private sealed class SamplingRow
    {
        public string PlanRevisionId { get; set; } = "";
        public string PlanId { get; set; } = "";
        public int RevisionNo { get; set; }
        public string InspectionMode { get; set; } = "";
        public int LotSizeMin { get; set; }
        public int? LotSizeMax { get; set; }
        public int? SampleSize { get; set; }
        public int AcceptanceNo { get; set; }
        public int RejectionNo { get; set; }
        public decimal Aql { get; set; }
        public string StandardName { get; set; } = "";
        public string StandardVersion { get; set; } = "";
        public DateTime EffectiveFrom { get; set; }
        public SamplingPlanRevision ToDomain() => new(PlanRevisionId, PlanId, RevisionNo,
            Enum.Parse<InspectionSamplingMode>(InspectionMode), LotSizeMin, LotSizeMax,
            SampleSize, AcceptanceNo, RejectionNo, Aql, StandardName, StandardVersion, EffectiveFrom);
    }
}
