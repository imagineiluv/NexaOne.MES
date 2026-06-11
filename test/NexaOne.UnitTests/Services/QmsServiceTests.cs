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
        Mock<ISpcParamRepository> spcRepo) =>
        new(defectRepo.Object, classRepo.Object, specRepo.Object, resultRepo.Object, spcRepo.Object);

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
        result.Value.MeasureType.Should().Be("Numeric");
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

    // ── SpcParam ──────────────────────────────────────────────────────────────

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
