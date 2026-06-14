using System.Text.Json;
using NexaOne.POM.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class ProductionPlanTests
{
    private static readonly DateTime Start = new(2026, 2, 1);
    private static readonly DateTime End   = new(2026, 2, 28);

    private static ProductionPlan Draft() =>
        ProductionPlan.Create("PP001", "Feb Plan", "P01", "PROD-A", 1000m, Start, End).Value;

    [Fact]
    public void Create_plan_starts_in_Draft()
    {
        var p = Draft();
        p.Status.Should().Be(ProductionPlanStatus.Draft);
        p.PlannedQty.Should().Be(1000m);
    }

    [Fact]
    public void Create_with_zero_qty_fails()
    {
        ProductionPlan.Create("PP002", "name", "P01", "PROD-A", 0, Start, End)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_with_start_after_end_fails()
    {
        ProductionPlan.Create("PP003", "name", "P01", "PROD-A", 100m, End, Start)
            .IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Release_moves_to_Released()
    {
        var p = Draft();
        p.Release().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.Released);
    }

    [Fact]
    public void Release_from_non_Draft_fails()
    {
        var p = Draft();
        p.Release();
        p.Release().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Start_requires_Released()
    {
        var p = Draft();
        p.Start().IsFailure.Should().BeTrue();
        p.Release();
        p.Start().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.InProgress);
    }

    [Fact]
    public void Complete_requires_InProgress()
    {
        var p = Draft();
        p.Release();
        p.Complete().IsFailure.Should().BeTrue();
        p.Start();
        p.Complete().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.Completed);
    }

    [Fact]
    public void Cancel_from_Completed_fails()
    {
        var p = Draft();
        p.Release();
        p.Start();
        p.Complete();
        p.Cancel().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Cancel_from_Draft_succeeds()
    {
        var p = Draft();
        p.Cancel().IsSuccess.Should().BeTrue();
        p.Status.Should().Be(ProductionPlanStatus.Cancelled);
    }

    // ── ADR-002: 상태 전이 시 ProductionPlanStatusChanged 도메인 이벤트 발행(outbox 봉투 검증) ──

    [Fact]
    public void Release_raises_StatusChanged_event_with_outbox_envelope()
    {
        var p = Draft();
        p.Release();

        var ev = p.DomainEvents.OfType<ProductionPlanStatusChangedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("ProductionPlanStatusChanged");
        ev.Module.Should().Be("POM");
        ev.AggregateId.Should().Be("PP001", "계획별 순서 보장을 위해 AGGREGATE_ID는 PlanId(PLAN_ID)여야 한다");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewStatus").GetString().Should().Be("Released");
    }

    [Fact]
    public void Start_raises_StatusChanged_event_carrying_InProgress()
    {
        var p = Draft();
        p.Release();
        p.ClearDomainEvents();   // Release 이벤트 제거 — Start 이벤트만 검증
        p.Start();

        var ev = p.DomainEvents.OfType<ProductionPlanStatusChangedDomainEvent>().Should().ContainSingle().Subject;
        ev.NewStatus.Should().Be(ProductionPlanStatus.InProgress);
        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewStatus").GetString().Should().Be("InProgress");
    }

    [Fact]
    public void Complete_raises_StatusChanged_event_carrying_Completed()
    {
        var p = Draft();
        p.Release();
        p.Start();
        p.ClearDomainEvents();
        p.Complete();

        var ev = p.DomainEvents.OfType<ProductionPlanStatusChangedDomainEvent>().Should().ContainSingle().Subject;
        ev.NewStatus.Should().Be(ProductionPlanStatus.Completed);
        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewStatus").GetString().Should().Be("Completed");
    }

    [Fact]
    public void Cancel_raises_StatusChanged_event_carrying_Cancelled()
    {
        var p = Draft();
        p.ClearDomainEvents();   // Create는 이벤트가 없지만 일관성 위해 비운다
        p.Cancel();

        var ev = p.DomainEvents.OfType<ProductionPlanStatusChangedDomainEvent>().Should().ContainSingle().Subject;
        ev.NewStatus.Should().Be(ProductionPlanStatus.Cancelled);
        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("NewStatus").GetString().Should().Be("Cancelled");
    }

    [Fact]
    public void Create_raises_no_domain_events()
        => Draft().DomainEvents.Should().BeEmpty("생성(Create)은 상태 전이가 아니므로 이벤트를 발행하지 않는다");

    [Fact]
    public void Restore_raises_no_domain_events()
        => ProductionPlan.Restore("PP-R", "name", "P01", "PROD-A", 100m, Start, End,
                ProductionPlanStatus.InProgress, null)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var p = Draft();
        p.Release();
        p.ClearDomainEvents();
        p.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }
}
