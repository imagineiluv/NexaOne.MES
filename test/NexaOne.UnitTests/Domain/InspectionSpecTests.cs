using NexaOne.QMS.Domain;

namespace NexaOne.UnitTests.Domain;

/// <summary>InspectionSpec 애그리거트 — 읽기경로 상태손실 회귀 방지.
/// Create는 IsActive를 항상 true로 강제하므로, 리포의 Row.ToDomain()이 Create를 쓰면
/// 비활성 스펙이 활성으로 잘못 복원된다. Restore는 영속 IsActive를 그대로 보존해야 한다.</summary>
public sealed class InspectionSpecTests
{
    [Fact]
    public void Restore_preserves_inactive_IsActive()
    {
        var spec = InspectionSpec.Restore(
            "SPEC001", "두께 검사", "PROC001", "두께", "Numeric",
            10m, 0.5m, 0.5m, isActive: false);

        spec.IsActive.Should().BeFalse("영속된 IS_ACTIVE=0은 읽기경로에서 비활성으로 보존돼야 한다");
    }

    [Fact]
    public void Restore_keeps_all_persisted_columns()
    {
        var spec = InspectionSpec.Restore(
            "SPEC002", "외관 검사", "PROC002", "외관", "Attribute",
            12.5m, 1.0m, 0.8m, isActive: true);

        spec.Id.Should().Be("SPEC002");
        spec.SpecName.Should().Be("외관 검사");
        spec.ProcessId.Should().Be("PROC002");
        spec.ItemName.Should().Be("외관");
        spec.MeasureType.Should().Be("Attribute");
        spec.NominalValue.Should().Be(12.5m);
        spec.TolerancePlus.Should().Be(1.0m);
        spec.ToleranceMinus.Should().Be(0.8m);
        spec.IsActive.Should().BeTrue();
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => InspectionSpec.Restore("SPEC003", "검사", "PROC001", "항목", "Numeric", null, null, null, isActive: false)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");
}
