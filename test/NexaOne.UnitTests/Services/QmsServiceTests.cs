using Moq;
using NexaOne.QMS.Application.Qms;
using NexaOne.QMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class QmsServiceTests
{
    private static InspectionSpec NumericSpec(string id = "SPEC001") =>
        InspectionSpec.Create(id, "두께 검사", "PROC001", "두께", "Numeric", 10m, 0.5m, 0.5m).Value;

    private static InspectionSpec AttributeSpec(string id = "SPEC002") =>
        InspectionSpec.Create(id, "외관 검사", "PROC001", "외관", "Attribute").Value;

    private static SpcParam TestParam(string id = "SPC001") =>
        SpcParam.Create(id, "두께 SPC", "EQ001", "PROC001", 10m, 10.3m, 9.7m, 5).Value;

    private QmsService BuildService(
        Mock<IDefectRepository> defectRepo,
        Mock<IDefectClassRepository> classRepo,
        Mock<IInspectionSpecRepository> specRepo,
        Mock<IInspectionResultRepository> resultRepo,
        Mock<ISpcParamRepository> spcRepo,
        Mock<IQmsReferenceRepository>? references = null) =>
        references is null
            ? new(defectRepo.Object, classRepo.Object, specRepo.Object, resultRepo.Object, spcRepo.Object)
            : new(defectRepo.Object, classRepo.Object, specRepo.Object, resultRepo.Object, spcRepo.Object, references.Object);

    // ── DefectClass ───────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateDefectClass_valid_severity_succeeds()
    {
        var classRepo = new Mock<IDefectClassRepository>();
        classRepo.Setup(r => r.AddAsync(It.IsAny<DefectClass>(), default)).Returns(Task.CompletedTask);

        var svc = BuildService(new(), classRepo, new(), new(), new());
        var result = await svc.CreateDefectClassAsync("DC001", "스크래치", "표면 스크래치", "Minor");

        result.IsSuccess.Should().BeTrue();
        result.Value.Severity.Should().Be("Minor");
        classRepo.Verify(r => r.AddAsync(It.IsAny<DefectClass>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateDefectClass_invalid_severity_fails()
    {
        var svc = BuildService(new(), new(), new(), new(), new());
        var result = await svc.CreateDefectClassAsync("DC001", "스크래치", "desc", "Medium");
        result.IsFailure.Should().BeTrue();
    }

    // ── InspectionSpec ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateInspectionSpec_numeric_succeeds()
    {
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.AddAsync(It.IsAny<InspectionSpec>(), default)).Returns(Task.CompletedTask);

        var svc = BuildService(new(), new(), specRepo, new(), new());
        var result = await svc.CreateInspectionSpecAsync("SPEC001", "두께", "PROC001", "두께", "Numeric", 10m, 0.5m, 0.5m);

        result.IsSuccess.Should().BeTrue();
        result.Value.MeasureType.Should().Be("Variable");
        specRepo.Verify(r => r.AddAsync(It.IsAny<InspectionSpec>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateInspectionSpec_invalid_measure_type_fails()
    {
        var svc = BuildService(new(), new(), new(), new(), new());
        var result = await svc.CreateInspectionSpecAsync("SPEC001", "두께", "PROC001", "두께", "Count", null, null, null);
        result.IsFailure.Should().BeTrue();
    }

    // ── InspectionResult ──────────────────────────────────────────────────────

    [Fact]
    public async Task RecordInspectionResult_numeric_within_tolerance_passes()
    {
        var spec = NumericSpec();
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.GetByIdAsync("SPEC001", default)).ReturnsAsync(spec);
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.AddAsync(It.IsAny<InspectionResult>(), default)).Returns(Task.CompletedTask);

        var svc = BuildService(new(), new(), specRepo, resultRepo, new());
        var result = await svc.RecordInspectionResultAsync(
            "RES001", "SPEC001", "LOT001", "EQ001", "inspector01",
            measuredValue: 10.2m, attributeResult: null, isPass: null, remark: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPass.Should().BeTrue();
    }

    [Fact]
    public async Task RecordInspectionResult_numeric_out_of_tolerance_fails_inspection()
    {
        var spec = NumericSpec();
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.GetByIdAsync("SPEC001", default)).ReturnsAsync(spec);
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.AddAsync(It.IsAny<InspectionResult>(), default)).Returns(Task.CompletedTask);

        var svc = BuildService(new(), new(), specRepo, resultRepo, new());
        var result = await svc.RecordInspectionResultAsync(
            "RES001", "SPEC001", "LOT001", "EQ001", "inspector01",
            measuredValue: 15m, attributeResult: null, isPass: null, remark: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.IsPass.Should().BeFalse();
    }

    [Fact]
    public async Task RecordInspectionResult_spec_not_found_fails()
    {
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.GetByIdAsync("SPEC999", default)).ReturnsAsync((InspectionSpec?)null);

        var svc = BuildService(new(), new(), specRepo, new(), new());
        var result = await svc.RecordInspectionResultAsync(
            "RES001", "SPEC999", "LOT001", "EQ001", "inspector01", null, null, null, null);

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task RecordInspectionResult_inactive_spec_is_blocked()
    {
        var spec = InspectionSpec.Restore("SPEC-OFF", "Offline", "PROC001", "Length",
            "Variable", 10m, .5m, .5m, isActive: false);
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.GetByIdAsync("SPEC-OFF", default)).ReturnsAsync(spec);
        var resultRepo = new Mock<IInspectionResultRepository>();
        var svc = BuildService(new(), new(), specRepo, resultRepo, new());

        var result = await svc.RecordInspectionResultAsync(
            "RES-OFF", "SPEC-OFF", "LOT001", "EQ001", "inspector01", 10m, null, null, null);

        result.IsFailure.Should().BeTrue();
        resultRepo.Verify(r => r.AddAsync(It.IsAny<InspectionResult>(), default), Times.Never);
    }

    [Fact]
    public async Task RecordInspectionResult_orphan_lot_is_rejected_before_write()
    {
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.GetByIdAsync("SPEC001", default)).ReturnsAsync(NumericSpec());
        var resultRepo = new Mock<IInspectionResultRepository>();
        var references = new Mock<IQmsReferenceRepository>();
        references.Setup(r => r.LotExistsAsync("MISSING", default)).ReturnsAsync(false);
        var svc = BuildService(new(), new(), specRepo, resultRepo, new(), references);

        var result = await svc.RecordInspectionResultAsync(
            "RES-X", "SPEC001", "MISSING", "EQ001", "inspector01", 10m, null, null, null);

        result.IsFailure.Should().BeTrue();
        resultRepo.Verify(r => r.AddAsync(It.IsAny<InspectionResult>(), default), Times.Never);
    }

    // ── SpcParam ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Incoming", InspectionExecutionType.Incoming)]
    [InlineData("Shipping", InspectionExecutionType.Shipping)]
    public async Task RecordInspectionExecution_preserves_requested_type(
        string requestedType, InspectionExecutionType expectedType)
    {
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.GetByIdAsync("SPEC001", default)).ReturnsAsync(NumericSpec());
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.AddAsync(It.IsAny<InspectionResult>(), default)).Returns(Task.CompletedTask);
        var svc = BuildService(new(), new(), specRepo, resultRepo, new());

        var result = await svc.RecordInspectionExecutionAsync(
            requestedType, $"RES-{requestedType}", "SPEC001", "LOT001", "EQ001", "inspector01",
            measuredValue: 10m, attributeResult: null, isPass: null, remark: null);

        result.IsSuccess.Should().BeTrue();
        result.Value.InspectionType.Should().Be(expectedType);
        resultRepo.Verify(r => r.AddAsync(
            It.Is<InspectionResult>(inspection => inspection.InspectionType == expectedType), default),
            Times.Once);
    }

    [Fact]
    public async Task RecordInspectionExecution_invalid_type_is_rejected_before_lookup_or_write()
    {
        var specRepo = new Mock<IInspectionSpecRepository>();
        var resultRepo = new Mock<IInspectionResultRepository>();
        var svc = BuildService(new(), new(), specRepo, resultRepo, new());

        var result = await svc.RecordInspectionExecutionAsync(
            "Receiving", "RES-BAD", "SPEC001", "LOT001", "EQ001", "inspector01",
            measuredValue: 10m, attributeResult: null, isPass: null, remark: null);

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        specRepo.Verify(r => r.GetByIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        resultRepo.Verify(r => r.AddAsync(It.IsAny<InspectionResult>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task V2_without_sampling_plan_requires_full_lot_inspection()
    {
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.GetExecutionByIdempotencyKeyAsync("KEY-FULL", default))
            .ReturnsAsync((InspectionExecution?)null);
        var svc = BuildService(new(), new(), new(), resultRepo, new());

        var result = await svc.RecordInspectionExecutionV2Async(
            V2Command("KEY-FULL", lotQuantity: 100, sampleQuantity: 1), "qa1");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        resultRepo.Verify(r => r.AddExecutionAsync(
            It.IsAny<InspectionExecution>(), It.IsAny<InspectionExecutionHistory>(),
            It.IsAny<InspectionExecutionHistory?>(), default), Times.Never);
    }

    [Fact]
    public async Task V2_sampling_plan_accepts_one_defect_at_Ac_one()
    {
        var specRepo = new Mock<IInspectionSpecRepository>();
        specRepo.Setup(r => r.GetByIdAsync("SPEC002", default)).ReturnsAsync(AttributeSpec());
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.GetExecutionByIdempotencyKeyAsync("KEY-AC", default))
            .ReturnsAsync((InspectionExecution?)null);
        resultRepo.Setup(r => r.GetSamplingPlanRevisionAsync("PLAN-REV-1", default))
            .ReturnsAsync(SamplingPlanRevision.Create(
                "PLAN-REV-1", "PLAN-1", 1, InspectionSamplingMode.Sampling,
                1, 1000, 10, 1, 2, 1m, "ISO 2859-1", "2026", DateTime.UtcNow).Value);
        InspectionExecution? saved = null;
        resultRepo.Setup(r => r.AddExecutionAsync(
                It.IsAny<InspectionExecution>(), It.IsAny<InspectionExecutionHistory>(),
                It.IsAny<InspectionExecutionHistory?>(), default))
            .Callback<InspectionExecution, InspectionExecutionHistory,
                InspectionExecutionHistory?, CancellationToken>((execution, _, _, _) => saved = execution)
            .Returns(Task.CompletedTask);
        resultRepo.Setup(r => r.GetExecutionAsync(It.IsAny<string>(), default))
            .ReturnsAsync((InspectionExecution?)null);
        var svc = BuildService(new(), new(), specRepo, resultRepo, new());
        var command = V2Command(
            "KEY-AC", lotQuantity: 100, sampleQuantity: 10, defectQuantity: 1,
            samplingPlanRevisionId: "PLAN-REV-1",
            items: [new("SPEC002", null, "Pass", 10, 1, null)]);

        var result = await svc.RecordInspectionExecutionV2Async(command, "qa1");

        result.IsSuccess.Should().BeTrue();
        result.Value.Execution.IsPass.Should().BeTrue(
            "Ac=1인 샘플링 계획에서는 defect=1이 합격이며 item 판정과 defect count는 별도 증적이다");
        saved.Should().BeSameAs(result.Value.Execution);
        saved!.Items.Should().ContainSingle(x => x.IsPass && x.DefectQuantity == 1);
    }

    [Fact]
    public async Task V2_rejects_sampling_plan_revision_not_effective_at_inspection_time()
    {
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.GetExecutionByIdempotencyKeyAsync("KEY-FUTURE-PLAN", default))
            .ReturnsAsync((InspectionExecution?)null);
        resultRepo.Setup(r => r.GetSamplingPlanRevisionAsync("PLAN-FUTURE", default))
            .ReturnsAsync(SamplingPlanRevision.Create(
                "PLAN-FUTURE", "PLAN-1", 2, InspectionSamplingMode.Sampling,
                1, 1000, 10, 0, 1, 1m, "ISO 2859-1", "2027",
                DateTime.UtcNow.AddHours(1)).Value);
        var svc = BuildService(new(), new(), new(), resultRepo, new());

        var result = await svc.RecordInspectionExecutionV2Async(
            V2Command("KEY-FUTURE-PLAN", 100, 10, 0, "PLAN-FUTURE"), "qa1");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        resultRepo.Verify(r => r.AddExecutionAsync(
            It.IsAny<InspectionExecution>(), It.IsAny<InspectionExecutionHistory>(),
            It.IsAny<InspectionExecutionHistory?>(), default), Times.Never);
    }

    [Fact]
    public async Task Concurrent_cancellation_with_different_key_returns_conflict_instead_of_500()
    {
        var execution = ExistingExecution("QMSI-CANCEL", "KEY-CREATE", new string('a', 64));
        var winner = InspectionExecutionHistory.Create(
            "QMSE-WINNER", execution.InspectionId, InspectionExecutionEventType.Cancelled,
            "KEY-CANCEL-WINNER", new string('b', 64), "qa2", DateTime.UtcNow,
            execution.RootInspectionId, execution.ParentInspectionId, reason: "winner").Value;
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.GetHistoryByIdempotencyKeyAsync(
                execution.InspectionId, "KEY-CANCEL-CALLER", default))
            .ReturnsAsync((InspectionExecutionHistory?)null);
        resultRepo.Setup(r => r.GetExecutionAsync(execution.InspectionId, default))
            .ReturnsAsync(execution);
        resultRepo.Setup(r => r.AppendHistoryAsync(
                It.IsAny<InspectionExecutionHistory>(), default))
            .ThrowsAsync(new InvalidOperationException("filtered unique cancellation race"));
        resultRepo.Setup(r => r.GetCancellationHistoryAsync(execution.InspectionId, default))
            .ReturnsAsync(winner);
        var svc = BuildService(new(), new(), new(), resultRepo, new());

        var result = await svc.CancelInspectionExecutionV2Async(
            execution.InspectionId, "KEY-CANCEL-CALLER", "caller", "qa1");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    [Fact]
    public async Task V2_bridge_details_include_linked_AI_inference_and_append_only_reviews()
    {
        var execution = ExistingExecution("QMSI-AI", "KEY-AI", new string('a', 64));
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.GetExecutionAsync(execution.InspectionId, default))
            .ReturnsAsync(execution);
        var qms = BuildService(new(), new(), new(), resultRepo, new());

        var inference = AiInspectionInference.Create(
            "AI-1", "AI-KEY-1", "MV-1", execution.InspectionId,
            "https://images.local/evidence.png", new string('b', 64),
            AiRawVerdict.Pass, .98m, .9m, DateTime.UtcNow, new string('c', 64)).Value;
        var review = AiInspectionReview.Create(
            "AIR-1", inference.InferenceId, 1, "qa2", AiReviewVerdict.Pass,
            "image verified", DateTime.UtcNow).Value;
        var aiRepo = new Mock<IAiInspectionRepository>();
        aiRepo.Setup(r => r.GetInferencesByInspectionAsync(execution.InspectionId, default))
            .ReturnsAsync([inference]);
        aiRepo.Setup(r => r.GetReviewsAsync(inference.InferenceId, default))
            .ReturnsAsync([review]);
        var bridge = new QmsBridge(
            qms,
            new AdvancedQualityService(Mock.Of<IAdvancedQualityRepository>()),
            new AiInspectionService(aiRepo.Object));

        var result = await bridge.GetInspectionExecutionV2Async(execution.InspectionId);

        result.IsSuccess.Should().BeTrue();
        result.Value.AiEvidence.Should().ContainSingle();
        result.Value.AiEvidence[0].Inference.ImageSha256.Should().Be(new string('b', 64));
        result.Value.AiEvidence[0].Reviews.Should().ContainSingle(x =>
            x.ReviewerId == "qa2" && x.Reason == "image verified");
    }

    [Fact]
    public async Task V2_same_key_and_same_canonical_request_replays_saved_execution()
    {
        var command = V2Command("KEY-REPLAY");
        var hash = InspectionExecutionRequestHasher.Compute(command, "qa1");
        var existing = ExistingExecution("QMSI-EXISTING", "KEY-REPLAY", hash);
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.GetExecutionByIdempotencyKeyAsync("KEY-REPLAY", default))
            .ReturnsAsync(existing);
        var svc = BuildService(new(), new(), new(), resultRepo, new());

        var result = await svc.RecordInspectionExecutionV2Async(command, "qa1");

        result.IsSuccess.Should().BeTrue();
        result.Value.IsReplay.Should().BeTrue();
        result.Value.Execution.Should().BeSameAs(existing);
        resultRepo.Verify(r => r.AddExecutionAsync(
            It.IsAny<InspectionExecution>(), It.IsAny<InspectionExecutionHistory>(),
            It.IsAny<InspectionExecutionHistory?>(), default), Times.Never);
    }

    [Fact]
    public async Task V2_same_key_with_different_request_hash_conflicts()
    {
        var command = V2Command("KEY-CONFLICT");
        var existing = ExistingExecution("QMSI-EXISTING", "KEY-CONFLICT", new string('a', 64));
        var resultRepo = new Mock<IInspectionResultRepository>();
        resultRepo.Setup(r => r.GetExecutionByIdempotencyKeyAsync("KEY-CONFLICT", default))
            .ReturnsAsync(existing);
        var svc = BuildService(new(), new(), new(), resultRepo, new());

        var result = await svc.RecordInspectionExecutionV2Async(command with { Remark = "changed" }, "qa1");

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Conflict);
    }

    private static RecordInspectionExecutionCommand V2Command(
        string key,
        int lotQuantity = 10,
        int sampleQuantity = 10,
        int defectQuantity = 0,
        string? samplingPlanRevisionId = null,
        IReadOnlyList<InspectionExecutionItemCommand>? items = null)
        => new(
            key, InspectionExecutionType.Process, InspectionExecutionRelationType.Original,
            null, "LOT001", "EQ001", lotQuantity, sampleQuantity, defectQuantity,
            samplingPlanRevisionId,
            items ?? [new("SPEC001", 10m, null, sampleQuantity, defectQuantity, null)],
            null);

    private static InspectionExecution ExistingExecution(
        string inspectionId, string key, string hash)
    {
        var item = InspectionResult.Create(
            "QMSR-EXISTING", "SPEC001", "LOT001", "EQ001", DateTime.UtcNow, "qa1",
            10m, null, null, 10m, .5m, .5m, "Variable", null,
            InspectionExecutionType.Process, inspectionId, 10, 0).Value;
        return InspectionExecution.Restore(
            inspectionId, InspectionExecutionType.Process,
            InspectionExecutionRelationType.Original, inspectionId, null,
            "LOT001", "EQ001", 10, 10, 0, key, hash, DateTime.UtcNow,
            "qa1", true, null, null, [item], []);
    }

    [Fact]
    public async Task CreateSpcParam_valid_limits_succeeds()
    {
        var spcRepo = new Mock<ISpcParamRepository>();
        spcRepo.Setup(r => r.AddAsync(It.IsAny<SpcParam>(), default)).Returns(Task.CompletedTask);

        var svc = BuildService(new(), new(), new(), new(), spcRepo);
        var result = await svc.CreateSpcParamAsync("SPC001", "두께", "EQ001", "PROC001", 10m, 10.3m, 9.7m, 5, null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Ucl.Should().Be(10.3m);
        spcRepo.Verify(r => r.AddAsync(It.IsAny<SpcParam>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateSpcParam_ucl_less_than_lcl_fails()
    {
        var svc = BuildService(new(), new(), new(), new(), new());
        var result = await svc.CreateSpcParamAsync("SPC001", "두께", "EQ001", "PROC001", 10m, 9m, 11m, 5, null, null);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task UpdateSpcControlLimits_succeeds()
    {
        var param = TestParam();
        var spcRepo = new Mock<ISpcParamRepository>();
        spcRepo.Setup(r => r.GetByIdAsync("SPC001", default)).ReturnsAsync(param);
        spcRepo.Setup(r => r.UpdateAsync(param, default)).Returns(Task.CompletedTask);

        var svc = BuildService(new(), new(), new(), new(), spcRepo);
        var result = await svc.UpdateSpcControlLimitsAsync("SPC001", 10.5m, 10.8m, 10.2m);

        result.IsSuccess.Should().BeTrue();
        param.Mean.Should().Be(10.5m);
        spcRepo.Verify(r => r.UpdateAsync(param, default), Times.Once);
    }

    [Fact]
    public async Task UpdateSpcControlLimits_not_found_fails()
    {
        var spcRepo = new Mock<ISpcParamRepository>();
        spcRepo.Setup(r => r.GetByIdAsync("SPC999", default)).ReturnsAsync((SpcParam?)null);

        var svc = BuildService(new(), new(), new(), new(), spcRepo);
        var result = await svc.UpdateSpcControlLimitsAsync("SPC999", 10m, 10.3m, 9.7m);

        result.IsFailure.Should().BeTrue();
    }
}
