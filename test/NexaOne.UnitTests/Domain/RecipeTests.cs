using System.Text.Json;
using NexaOne.RMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Domain;

public sealed class RecipeTests
{
    private static Recipe Draft() =>
        Recipe.Create("R001", "Etch Recipe A", "desc", "EC-01").Value;

    [Fact]
    public void Create_recipe_starts_in_Draft()
    {
        var r = Draft();
        r.ApprovalState.Should().Be(RecipeApprovalState.Draft);
        r.Version.Should().Be(1);
    }

    [Fact]
    public void Create_recipe_with_empty_id_fails()
    {
        Recipe.Create("", "name", "desc", "EC-01").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Create_recipe_with_empty_class_fails()
    {
        Recipe.Create("R002", "name", "desc", "").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void RequestApproval_moves_to_Pending()
    {
        var r = Draft();
        r.RequestApproval().IsSuccess.Should().BeTrue();
        r.ApprovalState.Should().Be(RecipeApprovalState.WaitApproval);
    }

    [Fact]
    public void RequestApproval_from_non_Draft_fails()
    {
        var r = Draft();
        r.RequestApproval();
        r.RequestApproval().IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Approve1_moves_to_Approved1()
    {
        var r = Draft();
        r.RequestApproval();
        r.Approve1("approver1").IsSuccess.Should().BeTrue();
        r.ApprovalState.Should().Be(RecipeApprovalState.Approved1);
        r.FirstApproverId.Should().Be("approver1");
    }

    [Fact]
    public void Approve2_with_same_approver_fails()
    {
        var r = Draft();
        r.RequestApproval();
        r.Approve1("approver1");
        r.Approve2("approver1").IsFailure.Should().BeTrue();
    }

    [Fact]
    public void Full_approval_flow_reaches_Released()
    {
        var r = Draft();
        r.RequestApproval();
        r.Approve1("approver1");
        r.Approve2("approver2");
        r.Release("releaser").IsSuccess.Should().BeTrue();
        r.ApprovalState.Should().Be(RecipeApprovalState.Released);
        r.ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public void CreateNewVersion_increments_version_and_resets_to_Draft()
    {
        var r = Draft();
        var v2 = r.CreateNewVersion();
        v2.Version.Should().Be(2);
        v2.ApprovalState.Should().Be(RecipeApprovalState.Draft);
    }

    // ── ADR-002: 승인 워크플로 전이가 도메인 이벤트(outbox 봉투)를 발행하는지 검증 ──
    // 단계별로 RequestApproval→Approve1→Approve2→Release를 진행하며, 각 전이 직전 ClearDomainEvents로
    // 이전 이벤트를 비워 해당 전이의 이벤트 1건만 검증한다. AggregateId는 레시피별 순서 보장을 위해 RecipeId여야 한다.

    [Fact]
    public void RequestApproval_raises_RecipeApprovalRequested_with_outbox_envelope()
    {
        var r = Draft();
        r.RequestApproval();

        var ev = r.DomainEvents.OfType<RecipeApprovalRequestedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("RecipeApprovalRequested");
        ev.Module.Should().Be("RMS");
        ev.AggregateId.Should().Be("R001", "레시피별 순서 보장을 위해 AGGREGATE_ID는 RecipeId여야 한다");
        ev.Payload.Should().Be("WaitApproval", "승인요청 이벤트 payload는 전이 후 상태(평문)여야 한다");
    }

    [Fact]
    public void Approve1_raises_RecipeFirstApproved_with_first_approver()
    {
        var r = Draft();
        r.RequestApproval();
        r.ClearDomainEvents();   // 승인요청 이벤트 제거 — 1차 승인 이벤트만 검증

        r.Approve1("approver1");

        var ev = r.DomainEvents.OfType<RecipeFirstApprovedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("RecipeFirstApproved");
        ev.Module.Should().Be("RMS");
        ev.AggregateId.Should().Be("R001");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("State").GetString().Should().Be("Approved1");
        doc.RootElement.GetProperty("FirstApproverId").GetString().Should().Be("approver1");
    }

    [Fact]
    public void Approve2_raises_RecipeApproved_with_second_approver()
    {
        var r = Draft();
        r.RequestApproval();
        r.Approve1("approver1");
        r.ClearDomainEvents();

        r.Approve2("approver2");

        var ev = r.DomainEvents.OfType<RecipeApprovedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("RecipeApproved");
        ev.Module.Should().Be("RMS");
        ev.AggregateId.Should().Be("R001");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("State").GetString().Should().Be("Approved");
        doc.RootElement.GetProperty("SecondApproverId").GetString().Should().Be("approver2");
    }

    [Fact]
    public void Release_raises_RecipeReleased_with_releaser_and_timestamp()
    {
        var r = Draft();
        r.RequestApproval();
        r.Approve1("approver1");
        r.Approve2("approver2");
        r.ClearDomainEvents();

        r.Release("releaser");

        var ev = r.DomainEvents.OfType<RecipeReleasedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("RecipeReleased");
        ev.Module.Should().Be("RMS");
        ev.AggregateId.Should().Be("R001");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("State").GetString().Should().Be("Released");
        doc.RootElement.GetProperty("ReleaserId").GetString().Should().Be("releaser");
        doc.RootElement.GetProperty("ReleasedAt").GetDateTime().Should().Be(r.ReleasedAt!.Value);
    }

    [Fact]
    public void Reject_raises_RecipeRejected_with_reason()
    {
        var r = Draft();
        r.RequestApproval();
        r.ClearDomainEvents();

        r.Reject("missing parameter limits");

        var ev = r.DomainEvents.OfType<RecipeRejectedDomainEvent>().Should().ContainSingle().Subject;
        ev.EventType.Should().Be("RecipeRejected");
        ev.Module.Should().Be("RMS");
        ev.AggregateId.Should().Be("R001");

        using var doc = JsonDocument.Parse(ev.Payload);
        doc.RootElement.GetProperty("State").GetString().Should().Be("Rejected");
        doc.RootElement.GetProperty("Reason").GetString().Should().Be("missing parameter limits");
    }

    [Fact]
    public void Restore_raises_no_domain_events()
        => Recipe.Restore("R-RST", "name", "desc", "EC-01", 3, RecipeApprovalState.Approved,
                "a1", "a2", null)
            .DomainEvents.Should().BeEmpty("읽기경로 재구성(Restore)은 도메인 이벤트 발행 대상이 아니다");

    [Fact]
    public void ClearDomainEvents_empties_the_list()
    {
        var r = Draft();
        r.RequestApproval();
        r.ClearDomainEvents();
        r.DomainEvents.Should().BeEmpty("리포가 발행 후 이벤트를 비워야 재발행되지 않는다");
    }

    // ── 읽기경로 상태손실 가드: RecipeParam.ToDomain은 Create(검증)가 아닌 Restore(무검증)로 복원해야 한다.
    //    Create는 paramName이 공백이면 실패→.Value=null→행 유실. Restore는 영속값을 그대로 재구성한다.

    [Fact]
    public void RecipeParam_Restore_rebuilds_full_persisted_state()
    {
        var p = RecipeParam.Restore("P-1", "R001", "Temperature", "250", "C", 7);

        p.Id.Should().Be("P-1");
        p.RecipeId.Should().Be("R001");
        p.ParamName.Should().Be("Temperature");
        p.ParamValue.Should().Be("250");
        p.Unit.Should().Be("C");
        p.SortOrder.Should().Be(7);
    }

    [Fact]
    public void RecipeParam_Restore_keeps_rows_that_Create_would_reject()
    {
        // Create는 공백 paramName을 거부(Result.Failure)해 읽기경로에서 행을 통째로 유실시킨다.
        RecipeParam.Create("P-2", "R001", "", "v", "u", 0).IsFailure.Should().BeTrue(
            "Create는 공백 paramName을 검증으로 거부한다");

        // Restore는 검증 없이 영속값을 그대로 복원해 행을 보존한다.
        var restored = RecipeParam.Restore("P-2", "R001", "", "v", "u", 0);
        restored.ParamName.Should().Be("", "Restore는 영속된 공백 값을 그대로 보존해 행 유실을 막는다");
        restored.Id.Should().Be("P-2");
        restored.ParamValue.Should().Be("v");
    }
}
