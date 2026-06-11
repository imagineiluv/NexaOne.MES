using NexaOne.QMS.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class QmsDomainTests
{
    private static readonly DateTime Inspected = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);

    // ── Defect ────────────────────────────────────────────────────────────────

    [Fact]
    public void Create_defect_valid_succeeds()
    {
        var result = Defect.Create("DEF001", "LOT001", "EQ001", "DC001", 3, 0.03m, Inspected, "inspector01");
        result.IsSuccess.Should().BeTrue();
        result.Value.IsConfirmed.Should().BeFalse();
    }

    [Fact]
    public void Create_defect_negative_count_fails()
    {
        var result = Defect.Create("DEF001", "LOT001", "EQ001", "DC001", -1, 0m, Inspected, "inspector01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Confirm_defect_succeeds()
    {
        var defect = Defect.Create("DEF001", "LOT001", "EQ001", "DC001", 3, 0.03m, Inspected, "inspector01").Value;
        var result = defect.Confirm("mgr01");
        result.IsSuccess.Should().BeTrue();
        defect.IsConfirmed.Should().BeTrue();
        defect.ConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public void Confirm_already_confirmed_defect_fails()
    {
        var defect = Defect.Create("DEF001", "LOT001", "EQ001", "DC001", 3, 0.03m, Inspected, "inspector01").Value;
        defect.Confirm("mgr01");
        var result = defect.Confirm("mgr02");
        result.IsFailure.Should().BeTrue();
    }

    // ── DefectClass ───────────────────────────────────────────────────────────

    [Fact]
    public void Create_defect_class_critical_succeeds()
    {
        var result = DefectClass.Create("DC001", "균열", "표면 균열", "Critical");
        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
    }

    [Theory]
    [InlineData("Critical")]
    [InlineData("Major")]
    [InlineData("Minor")]
    public void Create_defect_class_all_valid_severities(string severity)
    {
        var result = DefectClass.Create("DC001", "분류", "설명", severity);
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_defect_class_invalid_severity_fails()
    {
        var result = DefectClass.Create("DC001", "분류", "설명", "Medium");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Deactivate_defect_class_sets_inactive_and_deleted()
    {
        var dc = DefectClass.Create("DC001", "분류", "설명", "Minor").Value;
        dc.Deactivate();
        dc.IsActive.Should().BeFalse();
        dc.IsDeleted.Should().BeTrue();
    }

    // ── InspectionSpec ────────────────────────────────────────────────────────

    [Fact]
    public void Create_numeric_spec_succeeds()
    {
        var result = InspectionSpec.Create("SPEC001", "두께 검사", "PROC001", "두께", "Numeric", 10m, 0.5m, 0.5m);
        result.IsSuccess.Should().BeTrue();
        result.Value.MeasureType.Should().Be("Numeric");
    }

    [Fact]
    public void Create_attribute_spec_succeeds()
    {
        var result = InspectionSpec.Create("SPEC002", "외관 검사", "PROC001", "외관", "Attribute");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_spec_invalid_measure_type_fails()
    {
        var result = InspectionSpec.Create("SPEC001", "검사", "PROC001", "항목", "Count");
        result.IsFailure.Should().BeTrue();
    }

    // ── SpcParam ──────────────────────────────────────────────────────────────

    [Fact]
    public void Create_spc_param_valid_succeeds()
    {
        var result = SpcParam.Create("SPC001", "두께", "EQ001", "PROC001", 10m, 10.3m, 9.7m, 5);
        result.IsSuccess.Should().BeTrue();
        result.Value.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Create_spc_param_ucl_less_than_lcl_fails()
    {
        var result = SpcParam.Create("SPC001", "두께", "EQ001", "PROC001", 10m, 9m, 11m, 5);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_spc_param_zero_sample_size_fails()
    {
        var result = SpcParam.Create("SPC001", "두께", "EQ001", "PROC001", 10m, 10.3m, 9.7m, 0);
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void UpdateControlLimits_updates_mean_ucl_lcl()
    {
        var param = SpcParam.Create("SPC001", "두께", "EQ001", "PROC001", 10m, 10.3m, 9.7m, 5).Value;
        var result = param.UpdateControlLimits(10.5m, 10.8m, 10.2m);
        result.IsSuccess.Should().BeTrue();
        param.Mean.Should().Be(10.5m);
        param.Ucl.Should().Be(10.8m);
        param.Lcl.Should().Be(10.2m);
    }
}
