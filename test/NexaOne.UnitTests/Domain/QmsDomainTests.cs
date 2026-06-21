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

    [Fact]
    public void Restore_defect_class_preserves_softdelete_and_audit_without_revalidation()
    {
        var created = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updated = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
        var deleted = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);

        // Create는 IsActive=true 강제 + Deactivate가 IsDeleted/DeletedAt을 결합 설정하므로 영속 상태가 손상된다.
        // Restore는 IsActive·IsDeleted·DeletedAt·감사값을 행값 그대로 독립 복원한다.
        var dc = DefectClass.Restore("DC-DEL", "폐기 분류", "설명", "Minor",
            isActive: false, isDeleted: true, deletedAt: deleted,
            createdBy: "seeder", createdAt: created, updatedBy: "editor", updatedAt: updated);

        dc.IsActive.Should().BeFalse("영속된 비활성 상태가 활성으로 둔갑하면 안 된다");
        dc.IsDeleted.Should().BeTrue("영속된 소프트삭제 상태를 그대로 복원해야 한다");
        dc.DeletedAt.Should().Be(deleted, "삭제 시각이 읽은 시각으로 손실되면 안 된다");
        dc.CreatedBy.Should().Be("seeder", "감사 메타데이터 보존(매 읽기 UtcNow/\"\" 리셋 없음)");
        dc.CreatedAt.Should().Be(created);
        dc.UpdatedBy.Should().Be("editor");
        dc.UpdatedAt.Should().Be(updated);
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
    public void Restore_spc_param_preserves_inactive_state()
    {
        // 읽기경로 상태손실 회귀 방지: Create는 IsActive를 항상 true로 강제하므로,
        // 비활성(IsActive=false)으로 영속된 행이 Restore로 복원될 때 그 값이 유지돼야 한다.
        var param = SpcParam.Restore("SPC001", "두께", "EQ001", "PROC001", 10m, 10.3m, 9.7m, 11m, 9m, 5, false);

        param.IsActive.Should().BeFalse("Restore는 영속된 IsActive를 신뢰해 복원해야 한다(Create처럼 true로 덮어쓰면 안 됨)");
        param.Usl.Should().Be(11m);
        param.Lsl.Should().Be(9m);
        param.SampleSize.Should().Be(5);
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

    // ── InspectionResult (읽기경로 Restore) ──────────────────────────────────────

    [Fact]
    public void Restore_preserves_persisted_IsPass_verdict()
    {
        // 읽기경로 상태손실 회귀 방지: Create는 nominalValue/measureType이 없는 읽기경로에서
        // IsPass를 재계산(else 분기 isPass ?? false)하므로, 영속된 합부 판정이 Restore로 그대로 복원돼야 한다.
        var restored = InspectionResult.Restore(
            "IR001", "SPEC001", "LOT001", "EQ001",
            measuredValue: 9.99m, attributeResult: null, inspectedAt: Inspected,
            inspectorId: "inspector01", isPass: true, remark: "수동 합격 처리");

        restored.IsPass.Should().BeTrue("검사 시점에 확정된 합부 판정은 읽기마다 재계산되지 않고 보존돼야 한다");
        restored.MeasuredValue.Should().Be(9.99m);
        restored.Remark.Should().Be("수동 합격 처리");
    }

    [Fact]
    public void Restore_does_not_recompute_a_stored_fail_into_pass()
    {
        // measuredValue가 공차 안이어도 저장된 판정이 불합격이면 불합격으로 남아야 한다(공차 변경·수동 판정 시나리오).
        var restored = InspectionResult.Restore(
            "IR002", "SPEC001", "LOT001", "EQ001",
            measuredValue: 10m, attributeResult: null, inspectedAt: Inspected,
            inspectorId: "inspector01", isPass: false, remark: null);

        restored.IsPass.Should().BeFalse("저장된 불합격 판정이 읽기경로에서 합격으로 뒤집히면 안 된다");
    }

    [Fact]
    public void Restore_inspection_result_raises_no_domain_events()
        => InspectionResult.Restore("IR003", "SPEC001", "LOT001", "EQ001",
                null, null, Inspected, "inspector01", true, null)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");
}
