using Moq;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class QmsAdvancedQualityTests
{
    private static readonly DateTime T0 = new(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string HashA = new('a', 64);
    private static readonly string HashB = new('b', 64);

    private static SpcControlLimitRevision Limits() =>
        SpcControlLimitRevision.Create("REV1", "P1", 1,
            SpcControlChartType.IndividualsMovingRange, 0m, 3m, -3m, T0, "golden").Value;

    private static IReadOnlyList<SpcObservation> Points(params decimal[] values) =>
        values.Select((v, i) => new SpcObservation($"O{i + 1}", "P1", "REV1", "SG1",
            i + 1, v, T0.AddMinutes(i))).ToList();

    [Fact]
    public void Western_electric_rule_1_detects_point_beyond_three_sigma()
        => SpcRuleEngine.Evaluate(Limits(), Points(0m, 3.01m))
            .Should().ContainSingle(x => x.RuleCode == SpcRuleCode.WesternElectric1 && x.ObservationId == "O2");

    [Fact]
    public void Western_electric_rule_2_detects_two_of_three_beyond_two_sigma()
        => SpcRuleEngine.Evaluate(Limits(), Points(2.1m, 0m, 2.2m))
            .Should().Contain(x => x.RuleCode == SpcRuleCode.WesternElectric2 && x.ObservationId == "O3");

    [Fact]
    public void Western_electric_rule_3_detects_four_of_five_beyond_one_sigma()
        => SpcRuleEngine.Evaluate(Limits(), Points(1.1m, 1.2m, 0m, 1.3m, 1.4m))
            .Should().Contain(x => x.RuleCode == SpcRuleCode.WesternElectric3 && x.ObservationId == "O5");

    [Fact]
    public void Western_electric_rule_4_detects_eight_points_on_same_side()
        => SpcRuleEngine.Evaluate(Limits(), Points(.1m, .2m, .1m, .2m, .1m, .2m, .1m, .2m))
            .Should().Contain(x => x.RuleCode == SpcRuleCode.WesternElectric4 && x.ObservationId == "O8");

    [Fact]
    public void Nelson_trend_detects_six_monotonic_points()
        => SpcRuleEngine.Evaluate(Limits(), Points(-2m, -1m, 0m, 1m, 2m, 2.5m))
            .Should().Contain(x => x.RuleCode == SpcRuleCode.NelsonTrend && x.ObservationId == "O6");

    [Fact]
    public void Nelson_alternating_detects_fourteen_alternating_points()
        => SpcRuleEngine.Evaluate(Limits(), Points(1m, -1m, 1m, -1m, 1m, -1m, 1m,
                -1m, 1m, -1m, 1m, -1m, 1m, -1m))
            .Should().Contain(x => x.RuleCode == SpcRuleCode.NelsonAlternating && x.ObservationId == "O14");

    [Fact]
    public void Rule_engine_ignores_points_from_another_limit_revision()
    {
        var other = new SpcObservation("OTHER", "P1", "REV2", "SG2", 1, 100m, T0);
        SpcRuleEngine.Evaluate(Limits(), [other]).Should().BeEmpty();
    }

    [Fact]
    public void Subgroup_exposes_stable_mean_and_range()
    {
        var subgroup = new SpcSubgroup("SG1", "P1", SpcControlChartType.XBarR,
            T0, Points(9m, 10m, 11m));
        subgroup.Mean.Should().Be(10m);
        subgroup.Range.Should().Be(2m);
    }

    [Fact]
    public void Sampling_plan_accepts_at_Ac_and_rejects_at_Re()
    {
        var plan = SamplingPlanRevision.Create("PR1", "PLAN1", 1, InspectionSamplingMode.Sampling,
            1, 1000, 80, 2, 3, 1m, "ISO 2859-1", "2026", T0).Value;
        SamplingPlanCalculator.Evaluate(plan, 500, 80, 2).Value.Disposition.Should().Be(SamplingDisposition.Accept);
        SamplingPlanCalculator.Evaluate(plan, 500, 80, 3).Value.Disposition.Should().Be(SamplingDisposition.Reject);
    }

    [Fact]
    public void Sampling_plan_is_inconclusive_until_required_sample_is_complete()
    {
        var plan = SamplingPlanRevision.Create("PR1", "PLAN1", 1, InspectionSamplingMode.Sampling,
            1, 100, 20, 0, 1, .1m, "ISO 2859-1", "2026", T0).Value;
        var result = SamplingPlanCalculator.Evaluate(plan, 50, 19, 0);
        result.Value.Disposition.Should().Be(SamplingDisposition.Inconclusive);
    }

    [Fact]
    public void Full_inspection_requires_the_entire_lot()
    {
        var plan = SamplingPlanRevision.Create("PR1", "PLAN1", 1, InspectionSamplingMode.Full,
            1, null, null, 0, 1, 0m, "Internal", "1", T0).Value;
        SamplingPlanCalculator.Evaluate(plan, 10, 9, 0).Value.Disposition.Should().Be(SamplingDisposition.Inconclusive);
        SamplingPlanCalculator.Evaluate(plan, 10, 10, 1).Value.Disposition.Should().Be(SamplingDisposition.Reject);
    }

    [Fact]
    public void Sampling_plan_rejects_invalid_Ac_Re_and_quantities()
    {
        SamplingPlanRevision.Create("PR1", "PLAN1", 1, InspectionSamplingMode.Sampling,
            1, 100, 10, 1, 3, 1m, "ISO", "1", T0).IsFailure.Should().BeTrue();
        var plan = SamplingPlanRevision.Create("PR2", "PLAN1", 2, InspectionSamplingMode.Sampling,
            1, 100, 10, 1, 2, 1m, "ISO", "1", T0).Value;
        SamplingPlanCalculator.Evaluate(plan, 50, 10, 11).IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Ai_models_validate_hash_URI_and_threshold()
    {
        AiInspectionModelVersion.Create("MV1", "M1", 1, "https://models.local/m1.onnx",
            HashA, .9m, T0).IsSuccess.Should().BeTrue();
        AiInspectionModelVersion.Create("MV1", "M1", 1, "relative", "bad",
            2m, T0).IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Identical_AI_inference_retry_returns_original_without_second_insert()
    {
        AiInspectionInference? stored = null;
        var model = AiInspectionModelVersion.Create("MV1", "M1", 1,
            "https://models.local/m1.onnx", HashA, .9m, T0).Value;
        var repo = new Mock<IAiInspectionRepository>();
        repo.Setup(x => x.GetInferenceByIdempotencyKeyAsync("KEY1", default)).ReturnsAsync(() => stored);
        repo.Setup(x => x.GetModelVersionAsync("MV1", default)).ReturnsAsync(model);
        repo.Setup(x => x.InspectionExistsAsync("INSP1", default)).ReturnsAsync(true);
        repo.Setup(x => x.AddInferenceAsync(It.IsAny<AiInspectionInference>(), default))
            .Callback<AiInspectionInference, CancellationToken>((x, _) => stored = x)
            .Returns(Task.CompletedTask);
        var service = new AiInspectionService(repo.Object);

        var first = await service.RecordInferenceAsync("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Pass, .95m, T0);
        var retry = await service.RecordInferenceAsync("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Pass, .95m, T0);

        first.IsSuccess.Should().BeTrue();
        retry.Value.Should().BeSameAs(stored);
        repo.Verify(x => x.AddInferenceAsync(It.IsAny<AiInspectionInference>(), default), Times.Once);
    }

    [Fact]
    public async Task Reused_AI_idempotency_key_with_different_hash_conflicts()
    {
        AiInspectionInference? stored = null;
        var model = AiInspectionModelVersion.Create("MV1", "M1", 1,
            "https://models.local/m1.onnx", HashA, .9m, T0).Value;
        var repo = new Mock<IAiInspectionRepository>();
        repo.Setup(x => x.GetInferenceByIdempotencyKeyAsync("KEY1", default)).ReturnsAsync(() => stored);
        repo.Setup(x => x.GetModelVersionAsync("MV1", default)).ReturnsAsync(model);
        repo.Setup(x => x.InspectionExistsAsync("INSP1", default)).ReturnsAsync(true);
        repo.Setup(x => x.AddInferenceAsync(It.IsAny<AiInspectionInference>(), default))
            .Callback<AiInspectionInference, CancellationToken>((x, _) => stored = x)
            .Returns(Task.CompletedTask);
        var service = new AiInspectionService(repo.Object);

        await service.RecordInferenceAsync("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Pass, .95m, T0);

        var result = await service.RecordInferenceAsync("I2", "KEY1", "MV1", "INSP1",
            "https://images.local/i2.png", HashB, AiRawVerdict.Fail, .99m, T0);

        result.IsFailure.Should().BeTrue();
        repo.Verify(x => x.AddInferenceAsync(It.IsAny<AiInspectionInference>(), default), Times.Once);
    }

    [Fact]
    public async Task Concurrent_identical_AI_inference_returns_database_winner()
    {
        AiInspectionInference? winner = null;
        var model = AiInspectionModelVersion.Create("MV1", "M1", 1,
            "https://models.local/m1.onnx", HashA, .9m, T0).Value;
        var repo = new Mock<IAiInspectionRepository>();
        repo.Setup(x => x.GetInferenceByIdempotencyKeyAsync("KEY1", default)).ReturnsAsync(() => winner);
        repo.Setup(x => x.GetModelVersionAsync("MV1", default)).ReturnsAsync(model);
        repo.Setup(x => x.InspectionExistsAsync("INSP1", default)).ReturnsAsync(true);
        repo.Setup(x => x.AddInferenceAsync(It.IsAny<AiInspectionInference>(), default))
            .Callback<AiInspectionInference, CancellationToken>((x, _) => winner = x)
            .ThrowsAsync(new InvalidOperationException("unique key race"));
        var service = new AiInspectionService(repo.Object);

        var result = await service.RecordInferenceAsync("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Pass, .95m, T0);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().BeSameAs(winner);
    }

    [Fact]
    public async Task AI_review_is_appended_with_monotonic_sequence_and_reviewer_audit()
    {
        var inference = AiInspectionInference.Create("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Unknown, .5m, .9m, T0, HashB).Value;
        var previous = AiInspectionReview.Create("R1", "I1", 1, "qa1",
            AiReviewVerdict.Fail, "uncertain", T0).Value;
        var repo = new Mock<IAiInspectionRepository>();
        repo.Setup(x => x.GetInferenceAsync("I1", default)).ReturnsAsync(inference);
        repo.Setup(x => x.GetReviewsAsync("I1", default)).ReturnsAsync([previous]);
        repo.Setup(x => x.AddReviewAsync(It.IsAny<AiInspectionReview>(), default)).Returns(Task.CompletedTask);
        var service = new AiInspectionService(repo.Object);

        var result = await service.ReviewAsync("R2", "I1", "qa2",
            AiReviewVerdict.Pass, "manual image review", T0.AddMinutes(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.ReviewSequence.Should().Be(2);
        result.Value.ReviewerId.Should().Be("qa2");
        repo.Verify(x => x.AddReviewAsync(result.Value, default), Times.Once);
    }

    [Fact]
    public async Task AI_inference_rejects_missing_inspection_and_model_not_yet_effective()
    {
        var futureModel = AiInspectionModelVersion.Create("MV1", "M1", 1,
            "https://models.local/m1.onnx", HashA, .9m, T0.AddMinutes(1)).Value;
        var repo = new Mock<IAiInspectionRepository>();
        repo.Setup(x => x.GetModelVersionAsync("MV1", default)).ReturnsAsync(futureModel);
        repo.Setup(x => x.GetInferenceByIdempotencyKeyAsync(It.IsAny<string>(), default))
            .ReturnsAsync((AiInspectionInference?)null);
        var service = new AiInspectionService(repo.Object);

        var future = await service.RecordInferenceAsync("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Pass, .95m, T0);

        future.IsFailure.Should().BeTrue();
        repo.Verify(x => x.InspectionExistsAsync(It.IsAny<string>(), default), Times.Never);

        repo.Setup(x => x.GetModelVersionAsync("MV1", default)).ReturnsAsync(
            AiInspectionModelVersion.Create("MV1", "M1", 1,
                "https://models.local/m1.onnx", HashA, .9m, T0).Value);
        repo.Setup(x => x.InspectionExistsAsync("INSP-MISSING", default)).ReturnsAsync(false);

        var missing = await service.RecordInferenceAsync("I2", "KEY2", "MV1", "INSP-MISSING",
            "https://images.local/i2.png", HashA, AiRawVerdict.Pass, .95m, T0);

        missing.IsFailure.Should().BeTrue();
        repo.Verify(x => x.AddInferenceAsync(It.IsAny<AiInspectionInference>(), default), Times.Never);
    }

    [Fact]
    public async Task Concurrent_AI_reviews_retry_sequence_and_both_evidence_rows_are_appended()
    {
        var inference = AiInspectionInference.Create("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Unknown, .5m, .9m, T0, HashB).Value;
        var rows = new List<AiInspectionReview>();
        var calls = 0;
        var repo = new Mock<IAiInspectionRepository>();
        repo.Setup(x => x.GetInferenceAsync("I1", default)).ReturnsAsync(inference);
        repo.Setup(x => x.GetReviewsAsync("I1", default))
            .ReturnsAsync(() => rows.ToArray());
        repo.Setup(x => x.GetReviewAsync("R-CALLER", default))
            .ReturnsAsync(() => rows.SingleOrDefault(x => x.ReviewId == "R-CALLER"));
        repo.Setup(x => x.AddReviewAsync(It.IsAny<AiInspectionReview>(), default))
            .Returns<AiInspectionReview, CancellationToken>((review, _) =>
            {
                calls++;
                if (calls == 1)
                {
                    rows.Add(AiInspectionReview.Create("R-WINNER", "I1", 1, "qa1",
                        AiReviewVerdict.Fail, "concurrent winner", T0).Value);
                    throw new InvalidOperationException("unique sequence race");
                }
                rows.Add(review);
                return Task.CompletedTask;
            });
        var service = new AiInspectionService(repo.Object);

        var result = await service.ReviewAsync("R-CALLER", "I1", "qa2",
            AiReviewVerdict.Pass, "second reviewer", T0.AddSeconds(1));

        result.IsSuccess.Should().BeTrue();
        result.Value.ReviewSequence.Should().Be(2);
        rows.Should().HaveCount(2).And.OnlyHaveUniqueItems(x => x.ReviewSequence);
    }

    [Fact]
    public async Task Concurrent_AI_review_ID_only_replays_same_semantic_request()
    {
        var inference = AiInspectionInference.Create("I1", "KEY1", "MV1", "INSP1",
            "https://images.local/i1.png", HashA, AiRawVerdict.Unknown, .5m, .9m, T0, HashB).Value;
        var sameWinner = AiInspectionReview.Create("R1", "I1", 1, "qa1",
            AiReviewVerdict.Pass, "verified", T0).Value;
        var repo = new Mock<IAiInspectionRepository>();
        repo.Setup(x => x.GetInferenceAsync("I1", default)).ReturnsAsync(inference);
        repo.Setup(x => x.GetReviewsAsync("I1", default))
            .ReturnsAsync(Array.Empty<AiInspectionReview>());
        repo.Setup(x => x.AddReviewAsync(It.IsAny<AiInspectionReview>(), default))
            .ThrowsAsync(new InvalidOperationException("review ID race"));
        repo.Setup(x => x.GetReviewAsync("R1", default)).ReturnsAsync(sameWinner);
        var service = new AiInspectionService(repo.Object);

        var replay = await service.ReviewAsync("R1", "I1", "qa1",
            AiReviewVerdict.Pass, "verified", T0);
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Should().BeSameAs(sameWinner);

        var conflict = await service.ReviewAsync("R1", "I1", "qa2",
            AiReviewVerdict.Fail, "different decision", T0.AddSeconds(1));
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Type.Should().Be(NexaOne.Common.ErrorType.Conflict);
    }

    [Fact]
    public async Task SPC_subgroup_service_evaluates_and_appends_actor_audit_once()
    {
        var repo = new Mock<IAdvancedQualityRepository>();
        repo.Setup(x => x.GetSubgroupByIdempotencyKeyAsync("K1", default))
            .ReturnsAsync((SpcSubgroupReplay?)null);
        repo.Setup(x => x.GetLimitRevisionAsync("REV1", default)).ReturnsAsync(Limits());
        repo.Setup(x => x.AddSubgroupEvaluationAsync(It.IsAny<SpcSubgroup>(), "K1",
            It.IsAny<string>(), "PDA", "operator1", It.IsAny<IReadOnlyList<SpcRuleViolation>>(), default))
            .Returns(Task.CompletedTask);
        var service = new AdvancedQualityService(repo.Object);

        var result = await service.EvaluateSubgroupAsync("SG1", "K1", "REV1", T0,
            [0m, 3.1m], "PDA", "operator1");

        result.IsSuccess.Should().BeTrue();
        result.Value.Violations.Should().Contain(x => x.RuleCode == SpcRuleCode.WesternElectric1);
        repo.VerifyAll();
    }

    [Fact]
    public async Task SPC_subgroup_reused_idempotency_with_different_request_conflicts()
    {
        var repo = new Mock<IAdvancedQualityRepository>();
        repo.Setup(x => x.GetSubgroupByIdempotencyKeyAsync("K1", default))
            .ReturnsAsync(new SpcSubgroupReplay("SG1", HashA));
        var service = new AdvancedQualityService(repo.Object);

        var result = await service.EvaluateSubgroupAsync("SG2", "K1", "REV1", T0,
            [1m], "PDA", "operator1");

        result.IsFailure.Should().BeTrue();
        repo.Verify(x => x.AddSubgroupEvaluationAsync(It.IsAny<SpcSubgroup>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<SpcRuleViolation>>(), default), Times.Never);
    }
}
