using NexaOne.CMMS.Domain;

namespace NexaOne.UnitTests.Domain;

public sealed class CmmsDomainTests
{
    private static readonly DateTime Scheduled = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);

    // ── MaintenancePlan ───────────────────────────────────────────────────────

    [Fact]
    public void Create_plan_valid_succeeds()
    {
        var result = MaintenancePlan.Create("MP001", "월간 PM", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01");
        result.IsSuccess.Should().BeTrue();
        result.Value.Status.Should().Be(MaintenancePlanStatus.Planned);
    }

    [Theory]
    [InlineData("PM")]
    [InlineData("CM")]
    public void Create_plan_valid_plan_types(string planType)
    {
        var result = MaintenancePlan.Create("MP001", "계획", "EQ001", planType, "Monthly", Scheduled, 4m, "tech01");
        result.IsSuccess.Should().BeTrue();
    }

    [Fact]
    public void Create_plan_invalid_plan_type_fails()
    {
        var result = MaintenancePlan.Create("MP001", "계획", "EQ001", "INVALID", "Monthly", Scheduled, 4m, "tech01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_plan_zero_duration_fails()
    {
        var result = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 0m, "tech01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_plan_from_Planned_transitions_to_InProgress()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        var result = plan.Start();
        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.InProgress);
    }

    [Fact]
    public void Complete_plan_from_InProgress_transitions_to_Completed()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        plan.Start();
        var result = plan.Complete();
        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.Completed);
    }

    [Fact]
    public void Complete_plan_from_Planned_fails()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        var result = plan.Complete();
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_plan_from_Planned_succeeds()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        var result = plan.Cancel();
        result.IsSuccess.Should().BeTrue();
        plan.Status.Should().Be(MaintenancePlanStatus.Cancelled);
    }

    [Fact]
    public void Cancel_plan_from_Completed_fails()
    {
        var plan = MaintenancePlan.Create("MP001", "계획", "EQ001", "PM", "Monthly", Scheduled, 4m, "tech01").Value;
        plan.Start();
        plan.Complete();
        var result = plan.Cancel();
        result.IsFailure.Should().BeTrue();
    }

    // ── SparePart ─────────────────────────────────────────────────────────────

    [Fact]
    public void Create_spare_part_valid_succeeds()
    {
        var result = SparePart.Create("SP001", "오일 필터", "P-001", "설명", "EA", 10m, 3m, 50m, "A-01");
        result.IsSuccess.Should().BeTrue();
        result.Value.IsLowStock.Should().BeFalse();
    }

    [Fact]
    public void Create_spare_part_max_stock_less_than_min_fails()
    {
        var result = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 5m, 10m, 5m, "A-01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_spare_part_negative_stock_fails()
    {
        var result = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", -1m, 3m, 50m, "A-01");
        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public void AdjustStock_increase_succeeds()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 10m, 3m, 50m, "A-01").Value;
        var result = part.AdjustStock(5m);
        result.IsSuccess.Should().BeTrue();
        part.CurrentStock.Should().Be(15m);
    }

    [Fact]
    public void AdjustStock_decrease_succeeds()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 10m, 3m, 50m, "A-01").Value;
        var result = part.AdjustStock(-7m);
        result.IsSuccess.Should().BeTrue();
        part.CurrentStock.Should().Be(3m);
    }

    [Fact]
    public void AdjustStock_below_zero_fails()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 5m, 3m, 50m, "A-01").Value;
        var result = part.AdjustStock(-10m);
        result.IsFailure.Should().BeTrue();
        part.CurrentStock.Should().Be(5m);
    }

    [Fact]
    public void IsLowStock_true_when_current_stock_at_or_below_min()
    {
        var part = SparePart.Create("SP001", "필터", "P-001", "설명", "EA", 3m, 3m, 50m, "A-01").Value;
        part.IsLowStock.Should().BeTrue();
    }

    // ── SparePart 읽기경로 복원(Restore) — 상태손실 방지 ────────────────────────
    // 기존 ToDomain은 Create 재검증을 거쳐, 영속된 부품이 통째로 null이 되거나(읽기에서 사라짐)
    // 감사필드가 초기화됐다. Restore는 영속 필드를 그대로 복원해야 한다.

    [Fact]
    public void Restore_keeps_all_persisted_fields_including_dropped_audit()
    {
        var createdAt = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        var updatedAt = new DateTime(2026, 5, 6, 7, 8, 9, DateTimeKind.Utc);

        var part = SparePart.Restore(
            "SP-R1", "오일 필터", "P-001", "정밀 여과용", "EA",
            12m, 3m, 50m, "A-01", "EQC-1",
            createdBy: "creator01", createdAt: createdAt,
            updatedBy: "editor02", updatedAt: updatedAt);

        part.Id.Should().Be("SP-R1");
        part.Description.Should().Be("정밀 여과용", "Description은 영속되지만 읽기경로에서 유실되던 필드다");
        part.EquipmentClassId.Should().Be("EQC-1");
        part.CreatedBy.Should().Be("creator01", "감사필드는 Create가 복원하지 않아 매 읽기마다 초기화되던 손실 대상이다");
        part.CreatedAt.Should().Be(createdAt);
        part.UpdatedBy.Should().Be("editor02");
        part.UpdatedAt.Should().Be(updatedAt);
    }

    [Fact]
    public void Restore_survives_rows_that_Create_would_reject()
    {
        // MaxStock <= MinStock 인 영속 행: Create는 실패→.Value가 null→읽기에서 부품이 통째로 사라짐.
        // Restore는 검증 없이 그대로 복원해야 한다.
        var part = SparePart.Restore(
            "SP-LEGACY", "구형 부품", "P-OLD", "", "EA",
            currentStock: 0m, minStock: 10m, maxStock: 5m, location: "B-02", equipmentClassId: null,
            createdBy: "legacy", createdAt: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            updatedBy: null, updatedAt: null);

        part.Should().NotBeNull("MaxStock<=MinStock 옛 데이터도 읽기경로에서 유실되면 안 된다");
        part.MinStock.Should().Be(10m);
        part.MaxStock.Should().Be(5m);
        part.IsLowStock.Should().BeTrue("CurrentStock(0) <= MinStock(10)");
    }
}
