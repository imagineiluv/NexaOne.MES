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

    private RecipeService BuildService(
        Mock<IRecipeRepository> repo,
        Mock<IRecipeParamRepository>? paramRepo = null) =>
        new(repo.Object, (paramRepo ?? new Mock<IRecipeParamRepository>()).Object);

    [Fact]
    public async Task GetRecipes_without_filters_delegates_as_unfiltered_list()
    {
        var recipes = new[] { DraftRecipe("RCP001"), DraftRecipe("RCP002") };
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetAsync(null, null, default)).ReturnsAsync(recipes);

        var result = await BuildService(repo).GetRecipesAsync(null, null);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().HaveCount(2);
        repo.Verify(r => r.GetAsync(null, null, default), Times.Once);
    }

    [Fact]
    public async Task GetRecipes_forwards_equipment_class_and_state_as_combined_filter()
    {
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetAsync("CLASS01", RecipeApprovalState.Released, default))
            .ReturnsAsync(new[] { ReleasedRecipe() });

        var result = await BuildService(repo).GetRecipesAsync(
            "CLASS01", RecipeApprovalState.Released);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().ContainSingle(recipe =>
            recipe.EquipmentClassId == "CLASS01"
            && recipe.ApprovalState == RecipeApprovalState.Released);
        repo.Verify(r => r.GetAsync("CLASS01", RecipeApprovalState.Released, default), Times.Once);
    }

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

    // ── GetCountByStateAsync ──────────────────────────────────────────────────
    // 대시보드 승인대기 집계가 전체 목록 대신 COUNT(*)를 쓰도록 서비스가 리포지토리 카운트로 위임하는지 검증.

    [Fact]
    public async Task GetCountByState_delegates_to_repository_and_returns_count()
    {
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetCountByStateAsync(RecipeApprovalState.WaitApproval, default)).ReturnsAsync(3);

        var count = await BuildService(repo).GetCountByStateAsync(RecipeApprovalState.WaitApproval);

        count.Should().Be(3);
        repo.Verify(r => r.GetCountByStateAsync(RecipeApprovalState.WaitApproval, default), Times.Once);
        // 목록 조회 경로(GetByStateAsync)는 더 이상 호출되지 않아야 한다(목록 적재 회피).
        repo.Verify(r => r.GetByStateAsync(It.IsAny<RecipeApprovalState>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── CreateNewVersionAsync ─────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Released_recipe_blocks_parameter_update_and_delete(bool update)
    {
        var recipe = ReleasedRecipe();
        var parameter = RecipeParam.Restore("PARAM01", recipe.Id, "Temperature", "180", "C", 1);
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var paramRepo = new Mock<IRecipeParamRepository>();
        paramRepo.Setup(r => r.GetByIdAsync(parameter.Id, default)).ReturnsAsync(parameter);
        var service = BuildService(repo, paramRepo);

        var result = update
            ? await service.UpdateParamAsync(parameter.Id, "190")
            : await service.DeleteParamAsync(parameter.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("Released");
        parameter.ParamValue.Should().Be("180");
        paramRepo.Verify(r => r.UpdateAsync(It.IsAny<RecipeParam>(), default), Times.Never);
        paramRepo.Verify(r => r.DeleteAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Parameter_mutation_returns_conflict_when_recipe_is_released_after_service_check(bool update)
    {
        var recipe = DraftRecipe();
        var parameter = RecipeParam.Restore("PARAM01", recipe.Id, "Temperature", "180", "C", 1);
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var paramRepo = new Mock<IRecipeParamRepository>();
        paramRepo.Setup(r => r.GetByIdAsync(parameter.Id, default)).ReturnsAsync(parameter);
        paramRepo.Setup(r => r.TryUpdateIfRecipeEditableAsync(parameter, default)).ReturnsAsync(false);
        paramRepo.Setup(r => r.TryDeleteIfRecipeEditableAsync(parameter.Id, default)).ReturnsAsync(false);

        var result = update
            ? await BuildService(repo, paramRepo).UpdateParamAsync(parameter.Id, "190")
            : await BuildService(repo, paramRepo).DeleteParamAsync(parameter.Id);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().ContainEquivalentOf("released");
        parameter.ParamValue.Should().Be("180", "a rejected guarded update must not leak a changed aggregate");
        paramRepo.Verify(r => r.UpdateAsync(It.IsAny<RecipeParam>(), default), Times.Never);
        paramRepo.Verify(r => r.DeleteAsync(It.IsAny<string>(), default), Times.Never);
    }

    [Fact]
    public async Task Parameter_add_returns_conflict_when_recipe_is_released_after_service_check()
    {
        var recipe = DraftRecipe();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var paramRepo = new Mock<IRecipeParamRepository>();
        paramRepo.Setup(r => r.TryAddIfRecipeEditableAsync(
                It.IsAny<RecipeParam>(), default))
            .ReturnsAsync(false);

        var result = await BuildService(repo, paramRepo).AddParamAsync(
            "PARAM01", recipe.Id, "Temperature", "180", "C", 1);

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().ContainEquivalentOf("released");
        paramRepo.Verify(r => r.AddAsync(It.IsAny<RecipeParam>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateNewVersion_released_recipe_succeeds()
    {
        var source = ReleasedRecipe("RCP001");
        var sourceParams = new[]
        {
            RecipeParam.Restore("PARAM01", source.Id, "Temperature", "180", "C", 1),
            RecipeParam.Restore("PARAM02", source.Id, "Duration", "30", "min", 2),
        };
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(source);
        Recipe? persistedHeader = null;
        IReadOnlyList<RecipeParam>? persistedParams = null;
        repo.Setup(r => r.AddVersionAsync(
                It.IsAny<Recipe>(), It.IsAny<IReadOnlyList<RecipeParam>>(), default))
            .Callback<Recipe, IReadOnlyList<RecipeParam>, CancellationToken>((header, parameters, _) =>
            {
                persistedHeader = header;
                persistedParams = parameters;
            })
            .Returns(Task.CompletedTask);
        var paramRepo = new Mock<IRecipeParamRepository>();
        paramRepo.Setup(r => r.GetByRecipeAsync(source.Id, default)).ReturnsAsync(sourceParams);

        var result = await BuildService(repo, paramRepo)
            .CreateNewVersionAsync("RCP001", "RCP001_v2");

        result.IsSuccess.Should().BeTrue();
        result.Value.Version.Should().Be(2);
        result.Value.ApprovalState.Should().Be(RecipeApprovalState.Draft);
        result.Value.Id.Should().Be("RCP001_v2");
        persistedHeader.Should().BeSameAs(result.Value);
        persistedParams.Should().HaveCount(2);
        var copiedParams = persistedParams!;
        copiedParams.Should().OnlyContain(parameter => parameter.RecipeId == "RCP001_v2");
        copiedParams.Select(parameter => parameter.Id).Should().OnlyHaveUniqueItems();
        copiedParams.Select(parameter => parameter.Id)
            .Should().NotIntersectWith(sourceParams.Select(parameter => parameter.Id));
        copiedParams.Select(parameter => (parameter.ParamName, parameter.ParamValue, parameter.Unit, parameter.SortOrder))
            .Should().Equal(sourceParams.Select(parameter =>
                (parameter.ParamName, parameter.ParamValue, parameter.Unit, parameter.SortOrder)));
        repo.Verify(r => r.AddAsync(It.IsAny<Recipe>(), It.IsAny<CancellationToken>()), Times.Never,
            "header만 별도 커밋하면 parameter 복사 실패 시 빈 버전이 남을 수 있다");
    }

    [Fact]
    public async Task CreateNewVersion_non_released_recipe_fails()
    {
        var source = DraftRecipe("RCP001");
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(source);

        var result = await BuildService(repo).CreateNewVersionAsync("RCP001", "RCP001_v2");

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.AddVersionAsync(
            It.IsAny<Recipe>(), It.IsAny<IReadOnlyList<RecipeParam>>(), default), Times.Never);
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
