using Moq;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using NexaOne.Common;
using NexaOne.ServiceContracts.Rms;

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

    private static Recipe RecipeAtState(RecipeApprovalState state)
    {
        var recipe = DraftRecipe();
        if (state == RecipeApprovalState.Draft) return recipe;
        recipe.RequestApproval();
        if (state == RecipeApprovalState.WaitApproval) return recipe;
        if (state == RecipeApprovalState.Rejected)
        {
            recipe.Reject("revise");
            return recipe;
        }
        recipe.Approve1("user01");
        if (state == RecipeApprovalState.Approved1) return recipe;
        recipe.Approve2("user02");
        if (state == RecipeApprovalState.Approved) return recipe;
        recipe.Release("user03");
        return recipe;
    }

    private RecipeService BuildService(
        Mock<IRecipeRepository> repo,
        Mock<IRecipeParamRepository>? paramRepo = null) =>
        new(repo.Object, (paramRepo ?? new Mock<IRecipeParamRepository>()).Object);

    private static RecipeCommandContext Context(string actor, string key = "recipe-command-key")
        => new(actor, key);

    private static RecipeParamUpdateCommand ParamUpdate(
        RecipeParam parameter, string newValue = "190", string key = "recipe-param-key")
        => new(parameter.Id, newValue, parameter.Version, key, "engineer-1");

    private static RecipeCreateCommand CreateCommand(
        string id = "RCP001", string key = "recipe-create-key")
        => new(id, "Test Recipe", "desc", "CLASS01", key, "engineer-1");

    private static RecipeParamAddCommand ParamAdd(
        string recipeId, string paramId = "PARAM02", string key = "recipe-param-add-key")
        => new(paramId, recipeId, "Pressure", "2", "bar", 2, key, "engineer-1");

    private static RecipeParamDeleteCommand ParamDelete(
        RecipeParam parameter, string key = "recipe-param-delete-key")
        => new(parameter.Id, parameter.Version, key, "engineer-1");

    private static RecipeVersionCreateCommand VersionCommand(
        string sourceId = "RCP001", string newId = "RCP001_v2",
        string key = "recipe-version-key")
        => new(sourceId, newId, key, "engineer-1");

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
        repo.Setup(r => r.TryAddAsync(
            It.IsAny<Recipe>(), It.IsAny<RecipeWriteRecord>(), default)).ReturnsAsync(true);

        var result = await BuildService(repo).CreateRecipeAsync(CreateCommand());

        result.IsSuccess.Should().BeTrue();
        result.Value.ApprovalState.Should().Be(RecipeApprovalState.Draft);
        result.Value.Version.Should().Be(1);
        repo.Verify(r => r.TryAddAsync(
            It.IsAny<Recipe>(), It.Is<RecipeWriteRecord>(w =>
                w.CommandType == "Create" && w.ActorId == "engineer-1"), default), Times.Once);
    }

    [Fact]
    public async Task CreateRecipe_missing_id_fails()
    {
        var repo = new Mock<IRecipeRepository>();
        var result = await BuildService(repo).CreateRecipeAsync(CreateCommand(id: ""));
        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.TryAddAsync(
            It.IsAny<Recipe>(), It.IsAny<RecipeWriteRecord>(), default), Times.Never);
    }

    // ── RequestApprovalAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task RequestApproval_draft_recipe_succeeds()
    {
        var recipe = DraftRecipe();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);
        repo.Setup(r => r.TryTransitionAsync(
                It.IsAny<Recipe>(), RecipeApprovalState.Draft,
                It.Is<RecipeTransitionWrite>(write =>
                    write.ActorId == "requester" && write.IdempotencyKey == "request-key"), default))
            .ReturnsAsync(true);

        var result = await BuildService(repo).RequestApprovalAsync(
            "RCP001", Context("requester", "request-key"));

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.WaitApproval);
    }

    [Fact]
    public async Task RequestApproval_not_found_returns_failure()
    {
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((Recipe?)null);

        var result = await BuildService(repo).RequestApprovalAsync(
            "RXXX", Context("requester", "missing-key"));

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
        repo.Setup(r => r.TryTransitionAsync(
                It.IsAny<Recipe>(), RecipeApprovalState.WaitApproval,
                It.Is<RecipeTransitionWrite>(write => write.ActorId == "approver01"), default))
            .ReturnsAsync(true);

        var result = await BuildService(repo).Approve1Async(
            "RCP001", Context("approver01"));

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.Approved1);
    }

    [Fact]
    public async Task Approve1_draft_recipe_fails()
    {
        var recipe = DraftRecipe();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);

        var result = await BuildService(repo).Approve1Async(
            "RCP001", Context("approver01"));

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.TryTransitionAsync(
            It.IsAny<Recipe>(), It.IsAny<RecipeApprovalState>(), It.IsAny<RecipeTransitionWrite>(),
            default), Times.Never);
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
        repo.Setup(r => r.TryTransitionAsync(
                It.IsAny<Recipe>(), RecipeApprovalState.Approved1,
                It.Is<RecipeTransitionWrite>(write => write.ActorId == "user02"), default))
            .ReturnsAsync(true);

        var result = await BuildService(repo).Approve2Async(
            "RCP001", Context("user02"));

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

        var result = await BuildService(repo).Approve2Async(
            "RCP001", Context("user01"));

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
        repo.Setup(r => r.TryTransitionAsync(
                It.IsAny<Recipe>(), RecipeApprovalState.Approved,
                It.Is<RecipeTransitionWrite>(write => write.ActorId == "user03"), default))
            .ReturnsAsync(true);

        var result = await BuildService(repo).ReleaseAsync(
            "RCP001", Context("user03"));

        result.IsSuccess.Should().BeTrue();
        recipe.ApprovalState.Should().Be(RecipeApprovalState.Released);
        recipe.ReleasedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Concurrent_approval_transition_loser_returns_conflict()
    {
        var recipe = RecipeAtState(RecipeApprovalState.Approved);
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        repo.Setup(r => r.TryTransitionAsync(
                It.IsAny<Recipe>(), RecipeApprovalState.Approved,
                It.Is<RecipeTransitionWrite>(write => write.ActorId == "user03"), default))
            .ReturnsAsync(false);

        var result = await BuildService(repo).ReleaseAsync(
            recipe.Id, Context("user03"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("RMS.Recipe.ConcurrentTransition");
        repo.Verify(r => r.TryTransitionAsync(
            It.IsAny<Recipe>(), RecipeApprovalState.Approved,
            It.Is<RecipeTransitionWrite>(write => write.ActorId == "user03"), default), Times.Once);
    }

    // ── RejectAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Reject_with_reason_succeeds()
    {
        var recipe = DraftRecipe();
        recipe.RequestApproval();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(recipe);
        repo.Setup(r => r.TryTransitionAsync(
                It.IsAny<Recipe>(), RecipeApprovalState.WaitApproval,
                It.Is<RecipeTransitionWrite>(write =>
                    write.ActorId == "reviewer" && write.Reason == "Does not meet spec"), default))
            .ReturnsAsync(true);

        var result = await BuildService(repo).RejectAsync(
            "RCP001", "Does not meet spec", Context("reviewer"));

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

        var result = await BuildService(repo).RejectAsync(
            "RCP001", "", Context("reviewer"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task Rejecting_an_already_rejected_recipe_with_a_new_key_is_not_a_noop_transition()
    {
        var recipe = RecipeAtState(RecipeApprovalState.Rejected);
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);

        var result = await BuildService(repo).RejectAsync(
            recipe.Id, "another reason", Context("reviewer", "new-reject-key"));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("already Rejected");
        repo.Verify(r => r.TryTransitionAsync(
            It.IsAny<Recipe>(), It.IsAny<RecipeApprovalState>(),
            It.IsAny<RecipeTransitionWrite>(), default), Times.Never);
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
    [InlineData(RecipeApprovalState.WaitApproval)]
    [InlineData(RecipeApprovalState.Approved1)]
    [InlineData(RecipeApprovalState.Approved)]
    [InlineData(RecipeApprovalState.Released)]
    [InlineData(RecipeApprovalState.Rejected)]
    public async Task Only_draft_recipe_can_add_update_or_delete_parameters(
        RecipeApprovalState state)
    {
        var recipe = RecipeAtState(state);
        var parameter = RecipeParam.Restore("PARAM01", recipe.Id, "Temperature", "180", "C", 1);
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var paramRepo = new Mock<IRecipeParamRepository>();
        paramRepo.Setup(r => r.GetByIdAsync(parameter.Id, default)).ReturnsAsync(parameter);
        var service = BuildService(repo, paramRepo);

        var add = await service.AddParamAsync(ParamAdd(recipe.Id));
        var update = await service.UpdateParamAsync(ParamUpdate(parameter));
        var delete = await service.DeleteParamAsync(ParamDelete(parameter));

        add.IsFailure.Should().BeTrue();
        update.IsFailure.Should().BeTrue();
        delete.IsFailure.Should().BeTrue();
        add.Error.Description.Should().Contain("Draft");
        update.Error.Description.Should().Contain("Draft");
        delete.Error.Description.Should().Contain("Draft");
        parameter.ParamValue.Should().Be("180");
        paramRepo.Verify(r => r.TryAddAsync(
            It.IsAny<RecipeParam>(), It.IsAny<RecipeParamWriteRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        paramRepo.Verify(r => r.TryUpdateAsync(
            It.IsAny<RecipeParamWriteRecord>(), It.IsAny<CancellationToken>()), Times.Never);
        paramRepo.Verify(r => r.TryDeleteAsync(
            It.IsAny<RecipeParamWriteRecord>(), It.IsAny<CancellationToken>()), Times.Never);
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
        paramRepo.Setup(r => r.TryUpdateAsync(
            It.IsAny<RecipeParamWriteRecord>(), default)).ReturnsAsync(false);
        paramRepo.Setup(r => r.TryDeleteAsync(
            It.IsAny<RecipeParamWriteRecord>(), default)).ReturnsAsync(false);

        var result = update
            ? await BuildService(repo, paramRepo).UpdateParamAsync(ParamUpdate(parameter))
            : await BuildService(repo, paramRepo).DeleteParamAsync(ParamDelete(parameter));

        result.IsFailure.Should().BeTrue();
        if (update)
            result.Error.Code.Should().Be("RMS.RecipeParam.ConcurrentUpdate");
        else
            result.Error.Code.Should().Be("RMS.RecipeParam.ConcurrentDelete");
        parameter.ParamValue.Should().Be("180", "a rejected guarded update must not leak a changed aggregate");
        paramRepo.Verify(r => r.TryDeleteAsync(
            It.IsAny<RecipeParamWriteRecord>(), default), update ? Times.Never() : Times.Once());
    }

    [Fact]
    public async Task Parameter_add_returns_conflict_when_recipe_is_released_after_service_check()
    {
        var recipe = DraftRecipe();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var paramRepo = new Mock<IRecipeParamRepository>();
        paramRepo.Setup(r => r.TryAddAsync(
                It.IsAny<RecipeParam>(), It.IsAny<RecipeParamWriteRecord>(), default))
            .ReturnsAsync(false);

        var result = await BuildService(repo, paramRepo).AddParamAsync(
            new RecipeParamAddCommand(
                "PARAM01", recipe.Id, "Temperature", "180", "C", 1,
                "add-race-key", "engineer-1"));

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().ContainEquivalentOf("Draft");
        paramRepo.Verify(r => r.TryAddAsync(
            It.IsAny<RecipeParam>(), It.IsAny<RecipeParamWriteRecord>(), default), Times.Once);
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
        repo.Setup(r => r.TryAddVersionAsync(
                It.IsAny<Recipe>(), It.IsAny<IReadOnlyList<RecipeParam>>(),
                It.IsAny<RecipeWriteRecord>(), default))
            .Callback<Recipe, IReadOnlyList<RecipeParam>, RecipeWriteRecord, CancellationToken>((header, parameters, _, _) =>
            {
                persistedHeader = header;
                persistedParams = parameters;
            })
            .ReturnsAsync(true);
        var paramRepo = new Mock<IRecipeParamRepository>();
        paramRepo.Setup(r => r.GetByRecipeAsync(source.Id, default)).ReturnsAsync(sourceParams);

        var result = await BuildService(repo, paramRepo)
            .CreateNewVersionAsync(VersionCommand());

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
        repo.Verify(r => r.TryAddVersionAsync(
            It.IsAny<Recipe>(), It.IsAny<IReadOnlyList<RecipeParam>>(),
            It.Is<RecipeWriteRecord>(w => w.ActorId == "engineer-1"), default), Times.Once,
            "header·parameter·command ledger는 하나의 repository transaction이어야 한다");
    }

    [Fact]
    public async Task CreateNewVersion_non_released_recipe_fails()
    {
        var source = DraftRecipe("RCP001");
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RCP001", default)).ReturnsAsync(source);

        var result = await BuildService(repo).CreateNewVersionAsync(VersionCommand());

        result.IsFailure.Should().BeTrue();
        repo.Verify(r => r.TryAddVersionAsync(
            It.IsAny<Recipe>(), It.IsAny<IReadOnlyList<RecipeParam>>(),
            It.IsAny<RecipeWriteRecord>(), default), Times.Never);
    }

    [Fact]
    public async Task CreateNewVersion_source_not_found_returns_failure()
    {
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync("RXXX", default)).ReturnsAsync((Recipe?)null);

        var result = await BuildService(repo).CreateNewVersionAsync(
            VersionCommand("RXXX", "RXXX_v2"));

        result.IsFailure.Should().BeTrue();
    }

    [Fact]
    public async Task CreateNewVersion_invalid_new_id_is_rejected_by_the_recipe_factory()
    {
        var source = ReleasedRecipe();
        var repo = new Mock<IRecipeRepository>();
        repo.Setup(r => r.GetByIdAsync(source.Id, default)).ReturnsAsync(source);

        var result = await BuildService(repo).CreateNewVersionAsync(
            VersionCommand(source.Id, "   ", "invalid-version-key"));

        result.IsFailure.Should().BeTrue();
        result.Error.Type.Should().Be(ErrorType.Validation);
        repo.Verify(r => r.TryAddVersionAsync(
            It.IsAny<Recipe>(), It.IsAny<IReadOnlyList<RecipeParam>>(),
            It.IsAny<RecipeWriteRecord>(), default), Times.Never);
    }
}
