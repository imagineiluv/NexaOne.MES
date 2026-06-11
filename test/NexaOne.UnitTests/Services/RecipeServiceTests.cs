using Moq;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using NexaOne.Common;

namespace NexaOne.UnitTests.Services;

public sealed class RecipeServiceTests
{
    private static Recipe DraftRecipe(string id = "RCP001") =>
        Recipe.Create(id, "Test Recipe", "desc", "CLASS01").Value;

    private static Recipe ReleasedRecipe(string id = "RCP001")
    {
        var r = DraftRecipe(id);
        r.RequestApproval();
        r.Approve1("user01");
        r.Approve2("user02");
        r.Release("user03");
        return r;
    }

    private RecipeService BuildService(Mock<IRecipeRepository> repo) =>
        new(repo.Object, new Mock<IRecipeParamRepository>().Object);

    // ── CreateRecipeAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRecipe_valid_data_succeeds()
    {
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.AddAsync(It.IsAny<Recipe>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreateRecipeAsync("RCP001", "Test Recipe", "desc", "CLASS01");

        result.IsSuccess.Should().BeTrue();
        result.Value.ApprovalState.Should().Be(RecipeApprovalState.Draft);
        result.Value.Version.Should().Be(1);
        repo.Verify(r => r.AddAsync(It.IsAny<Recipe>(), default), Times.Once);
    }

    [Fact]
    public async Task CreateRecipe_missing_id_fails()
    {
        var repo = new Mock<IRecipeRepository>();
        var result = await BuildService(repo).CreateRecipeAsync("", "Test Recipe", "desc", "CLASS01");
        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<Recipe>(), default), Times.Never);
    }

    // ── RequestApprovalAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task RequestApproval_draft_recipe_succeeds()
    {
        var recipe = DraftRecipe();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Recipe>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).RequestApprovalAsync("RCP001");

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.WaitApproval);
    }

    [Fact]
    public async Task RequestApproval_not_found_returns_failure()
    {
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((Recipe?)null);

        var result = await BuildService(repo).RequestApprovalAsync("RXXX");

        result.IsFailure.Should().BeTrue();
    }

    // ── Approve1Async ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve1_wait_approval_recipe_succeeds()
    {
        var recipe = DraftRecipe();
        recipe.RequestApproval();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Recipe>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).Approve1Async("RCP001", "approver01");

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.Approved1);
    }

    [Fact]
    public async Task Approve1_draft_recipe_fails()
    {
        var recipe = DraftRecipe();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);

        var result = await BuildService(repo).Approve1Async("RCP001", "approver01");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.UpdateAsync(It.IsAny<Recipe>(), default), Times.Never);
    }

    // ── Approve2Async ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Approve2_approved1_recipe_succeeds()
    {
        var recipe = DraftRecipe();
        recipe.RequestApproval();
        recipe.Approve1("user01");
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Recipe>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).Approve2Async("RCP001", "user02");

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.Approved);
    }

    [Fact]
    public async Task Approve2_same_approver_fails()
    {
        var recipe = DraftRecipe();
        recipe.RequestApproval();
        recipe.Approve1("user01");
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);

        var result = await BuildService(repo).Approve2Async("RCP001", "user01");

        result.IsFailure.Should().BeTrue();
    }

    // ── ReleaseAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Release_approved_recipe_succeeds()
    {
        var recipe = DraftRecipe();
        recipe.RequestApproval();
        recipe.Approve1("user01");
        recipe.Approve2("user02");
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Recipe>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).ReleaseAsync("RCP001", "user03");

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.Released);
        recipe.ReleasedAt.Should().NotBeNull();
    }

    // ── RejectAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_with_reason_succeeds()
    {
        var recipe = DraftRecipe();
        recipe.RequestApproval();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);
        repo.Setup(r => r.UpdateAsync(It.IsAny<Recipe>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).RejectAsync("RCP001", "Does not meet spec");

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.Rejected);
    }

    [Fact]
    public async Task Reject_empty_reason_fails()
    {
        var recipe = DraftRecipe();
        recipe.RequestApproval();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);

        var result = await BuildService(repo).RejectAsync("RCP001", "");

        result.IsFailure.Should().BeTrue();
    }

    // ── CreateNewVersionAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task CreateNewVersion_released_recipe_succeeds()
    {
        var source = ReleasedRecipe("RCP001");
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(source);
        repo.Setup(r => r.AddAsync(It.IsAny<Recipe>(), default)).Returns(Task.CompletedTask);

        var result = await BuildService(repo).CreateNewVersionAsync("RCP001", "RCP001_v2");

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be(2);
        result.Value.ApprovalState.Should().Be(RecipeApprovalState.Draft);
        result.Value.Id.Should().Be("RCP001_v2");
    }

    [Fact]
    public async Task CreateNewVersion_non_released_recipe_fails()
    {
        var source = DraftRecipe("RCP001");
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(source);

        var result = await BuildService(repo).CreateNewVersionAsync("RCP001", "RCP001_v2");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddAsync(It.IsAny<Recipe>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateNewVersion_source_not_found_returns_failure()
    {
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((Recipe?)null);

        var result = await BuildService(repo).CreateNewVersionAsync("RXXX", "RXXX_v2");

        result.IsFailure.Should().BeTrue();
    }
}
