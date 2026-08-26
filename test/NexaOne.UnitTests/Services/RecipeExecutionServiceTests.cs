using System.Text.Json;
using Moq;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.UnitTests.Services;

public sealed class RecipeExecutionServiceTests
{
    private static Recipe RecipeOf(RecipeApprovalState state = RecipeApprovalState.Draft)
    {
        var recipe = Recipe.Create("RCP01", "Wash", "Carrier wash", "WASHER").Value;
        if (state == RecipeApprovalState.Released)
        {
            recipe.RequestApproval();
            recipe.Approve1("approver-1");
            recipe.Approve2("approver-2");
            recipe.Release("releaser");
        }
        return recipe;
    }

    [Fact]
    public async Task Assign_requires_exactly_one_equipment_scope()
    {
        var recipes = new Mock<IRecipeRepository>();
        var parameters = new Mock<IRecipeParamRepository>();
        var executions = new Mock<IRecipeExecutionRepository>();
        var service = new RecipeExecutionService(recipes.Object, parameters.Object, executions.Object);

        var neither = await service.AssignAsync(new RecipeAssignmentCommand(
            "A1", null, null, "RCP01", 1), "operator");
        var both = await service.AssignAsync(new RecipeAssignmentCommand(
            "A2", "EQ01", "WASHER", "RCP01", 1), "operator");

        neither.IsFailure.Should().BeTrue();
        both.IsFailure.Should().BeTrue();
        executions.Verify(r => r.TrySaveReleasedAssignmentAsync(
            It.IsAny<RecipeEquipmentAssignment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Assign_equipment_class_saves_actor_and_recipe_version()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();
        RecipeEquipmentAssignment? saved = null;
        executions.Setup(r => r.TrySaveReleasedAssignmentAsync(It.IsAny<RecipeEquipmentAssignment>(), default))
            .Callback<RecipeEquipmentAssignment, CancellationToken>((value, _) => saved = value)
            .ReturnsAsync(true);
        var service = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object, executions.Object);

        var result = await service.AssignAsync(new RecipeAssignmentCommand(
            "A1", null, "WASHER", recipe.Id, recipe.Version), "operator-1");

        result.IsSuccess.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.EquipmentId.Should().BeNull();
        saved.EquipmentClassId.Should().Be("WASHER");
        saved.AssignedBy.Should().Be("operator-1");
        saved.RecipeVersion.Should().Be(1);
    }

    [Fact]
    public async Task Assign_rejects_non_released_recipe()
    {
        var recipe = RecipeOf();
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();
        var service = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object, executions.Object);

        var result = await service.AssignAsync(new RecipeAssignmentCommand(
            "A1", null, "WASHER", recipe.Id, recipe.Version), "operator-1");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("Released");
        executions.Verify(r => r.TrySaveReleasedAssignmentAsync(
            It.IsAny<RecipeEquipmentAssignment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Assign_fails_closed_without_an_explicit_or_authenticated_actor()
    {
        var previousActor = CurrentUserContext.UserId;
        try
        {
            CurrentUserContext.UserId = null;
            var recipe = RecipeOf(RecipeApprovalState.Released);
            var recipes = new Mock<IRecipeRepository>();
            recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
            var executions = new Mock<IRecipeExecutionRepository>();
            var service = new RecipeExecutionService(
                recipes.Object, new Mock<IRecipeParamRepository>().Object, executions.Object);

            var result = await service.AssignAsync(new RecipeAssignmentCommand(
                "A1", null, "WASHER", recipe.Id, recipe.Version));

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
            executions.Verify(r => r.TrySaveReleasedAssignmentAsync(
                It.IsAny<RecipeEquipmentAssignment>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            CurrentUserContext.UserId = previousActor;
        }
    }

    [Fact]
    public async Task RecordExecution_rejects_non_released_recipe()
    {
        var recipe = RecipeOf();
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();
        var service = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object, executions.Object);

        var result = await service.RecordExecutionAsync(Command(recipe), "operator-1");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("Released");
        executions.Verify(r => r.TryAddExecutionAsync(
            It.IsAny<RecipeExecutionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordExecution_fails_closed_without_an_explicit_or_authenticated_actor()
    {
        var previousActor = CurrentUserContext.UserId;
        try
        {
            CurrentUserContext.UserId = null;
            var recipe = RecipeOf(RecipeApprovalState.Released);
            var recipes = new Mock<IRecipeRepository>();
            recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
            var parameters = new Mock<IRecipeParamRepository>();
            parameters.Setup(r => r.GetByRecipeAsync(recipe.Id, default))
                .ReturnsAsync(Array.Empty<RecipeParam>());
            var executions = new Mock<IRecipeExecutionRepository>();
            var service = new RecipeExecutionService(
                recipes.Object, parameters.Object, executions.Object);

            var result = await service.RecordExecutionAsync(Command(recipe));

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
            executions.Verify(r => r.TryAddExecutionAsync(
                It.IsAny<RecipeExecutionSnapshot>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            CurrentUserContext.UserId = previousActor;
        }
    }

    [Fact]
    public async Task RecordExecution_freezes_header_parameters_actor_and_idempotency_key()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var parameters = new Mock<IRecipeParamRepository>();
        parameters.Setup(r => r.GetByRecipeAsync(recipe.Id, default)).ReturnsAsync(new[]
        {
            RecipeParam.Restore("P2", recipe.Id, "Time", "30", "min", 2),
            RecipeParam.Restore("P1", recipe.Id, "Temperature", "180", "C", 1),
        });
        var executions = new Mock<IRecipeExecutionRepository>();
        RecipeExecutionSnapshot? saved = null;
        executions.Setup(r => r.GetExecutionByIdempotencyKeyAsync("idem-1", default))
            .ReturnsAsync((RecipeExecutionSnapshot?)null);
        executions.Setup(r => r.TryAddExecutionAsync(It.IsAny<RecipeExecutionSnapshot>(), default))
            .Callback<RecipeExecutionSnapshot, CancellationToken>((value, _) => saved = value)
            .ReturnsAsync(true);
        var service = new RecipeExecutionService(recipes.Object, parameters.Object, executions.Object);

        var result = await service.RecordExecutionAsync(Command(recipe), "operator-1");

        result.IsSuccess.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.IdempotencyKey.Should().Be("idem-1");
        saved.AppliedBy.Should().Be("operator-1");
        using var header = JsonDocument.Parse(saved.RecipeSnapshotJson);
        header.RootElement.GetProperty("recipeId").GetString().Should().Be(recipe.Id);
        header.RootElement.GetProperty("version").GetInt32().Should().Be(recipe.Version);
        using var paramJson = JsonDocument.Parse(saved.ParameterSnapshotJson);
        paramJson.RootElement.EnumerateArray()
            .Select(item => item.GetProperty("paramId").GetString())
            .Should().Equal("P1", "P2");
    }

    [Fact]
    public async Task RecordExecution_replays_same_idempotency_key_but_rejects_different_request()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var command = Command(recipe);
        var recipes = new Mock<IRecipeRepository>();
        var parameters = new Mock<IRecipeParamRepository>();
        var executions = new Mock<IRecipeExecutionRepository>();
        var service = new RecipeExecutionService(recipes.Object, parameters.Object, executions.Object);

        // First call establishes the canonical request hash.
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        parameters.Setup(r => r.GetByRecipeAsync(recipe.Id, default)).ReturnsAsync(Array.Empty<RecipeParam>());
        executions.Setup(r => r.GetExecutionByIdempotencyKeyAsync(command.IdempotencyKey, default))
            .ReturnsAsync((RecipeExecutionSnapshot?)null);
        RecipeExecutionSnapshot? stored = null;
        executions.Setup(r => r.TryAddExecutionAsync(It.IsAny<RecipeExecutionSnapshot>(), default))
            .Callback<RecipeExecutionSnapshot, CancellationToken>((value, _) => stored = value)
            .ReturnsAsync(true);
        (await service.RecordExecutionAsync(command, "operator-1")).IsSuccess.Should().BeTrue();

        executions.Setup(r => r.GetExecutionByIdempotencyKeyAsync(command.IdempotencyKey, default))
            .ReturnsAsync(() => stored);
        var replay = await service.RecordExecutionAsync(command, "operator-1");
        var collision = await service.RecordExecutionAsync(command with { EquipmentId = "EQ02" }, "operator-1");

        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        collision.IsFailure.Should().BeTrue();
        collision.Error.Description.Should().Contain("different");
    }

    private static RecipeExecutionCommand Command(Recipe recipe) => new(
        ExecutionId: "EXE01",
        IdempotencyKey: "idem-1",
        PlantId: "PLANT01",
        EquipmentId: "EQ01",
        RecipeId: recipe.Id,
        RecipeVersion: recipe.Version,
        AppliedAt: new DateTime(2026, 8, 26, 1, 2, 3, DateTimeKind.Utc),
        Source: "Equipment",
        ProcessLotId: "LOT01",
        WorkOrderId: "WO01",
        ProcessId: "PROC01",
        TraceId: "TRACE01",
        ConditionSnapshotJson: "{\"pressure\":2.1}");
}
