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
}
