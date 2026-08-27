using System.Text.Json;
using Moq;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using NexaOne.ServiceContracts.Mdm;
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

    private static Mock<IEquipmentDirectory> EquipmentDirectory(
        string equipmentId = "EQ01",
        string plantId = "PLANT01",
        string equipmentClassId = "WASHER",
        bool isValid = true)
    {
        var directory = new Mock<IEquipmentDirectory>();
        directory.Setup(value => value.GetEquipmentAsync(equipmentId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EquipmentDirectoryEntry(
                equipmentId, plantId, equipmentClassId, isValid));
        directory.Setup(value => value.EquipmentClassExistsAsync(
                equipmentClassId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        return directory;
    }

    private static RecipeEquipmentAssignment AssignmentFor(
        Recipe recipe,
        DateTime appliedAt,
        string equipmentId = "EQ01")
        => new(
            "ASSIGN01", equipmentId, null, recipe.Id, recipe.Version,
            appliedAt.AddMinutes(-1), null, "engineer-1", true);

    [Fact]
    public void Constructor_requires_equipment_directory()
    {
        Action create = () => new RecipeExecutionService(
            new Mock<IRecipeRepository>().Object,
            new Mock<IRecipeParamRepository>().Object,
            new Mock<IRecipeExecutionRepository>().Object,
            null!);

        create.Should().Throw<ArgumentNullException>()
            .WithParameterName("equipmentDirectory");
    }

    [Fact]
    public async Task Assign_requires_exactly_one_equipment_scope()
    {
        var recipes = new Mock<IRecipeRepository>();
        var parameters = new Mock<IRecipeParamRepository>();
        var executions = new Mock<IRecipeExecutionRepository>();
        var service = new RecipeExecutionService(
            recipes.Object, parameters.Object, executions.Object, EquipmentDirectory().Object);

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
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, EquipmentDirectory().Object);

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
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, EquipmentDirectory().Object);

        var result = await service.AssignAsync(new RecipeAssignmentCommand(
            "A1", null, "WASHER", recipe.Id, recipe.Version), "operator-1");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("Released");
        executions.Verify(r => r.TrySaveReleasedAssignmentAsync(
            It.IsAny<RecipeEquipmentAssignment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Assign_rejects_future_effective_date_before_repository_write()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();
        var service = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, EquipmentDirectory().Object);

        var result = await service.AssignAsync(new RecipeAssignmentCommand(
            "A1", "EQ01", null, recipe.Id, recipe.Version,
            DateTime.UtcNow.AddMinutes(5)), "operator-1");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().ContainEquivalentOf("future");
        executions.Verify(r => r.TrySaveReleasedAssignmentAsync(
            It.IsAny<RecipeEquipmentAssignment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Assign_equipment_scope_requires_active_matching_equipment_master()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();

        var inactiveDirectory = EquipmentDirectory(isValid: false);
        var inactive = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, inactiveDirectory.Object);
        var wrongClassDirectory = EquipmentDirectory(equipmentClassId: "ETCHER");
        var wrongClass = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, wrongClassDirectory.Object);
        var command = new RecipeAssignmentCommand(
            "A1", "EQ01", null, recipe.Id, recipe.Version);

        (await inactive.AssignAsync(command, "operator")).Error.Code
            .Should().Be("RMS.RecipeAssignment.EquipmentInactive");
        (await wrongClass.AssignAsync(command, "operator")).Error.Code
            .Should().Be("RMS.RecipeAssignment.EquipmentClassMismatch");
        executions.Verify(r => r.TrySaveReleasedAssignmentAsync(
            It.IsAny<RecipeEquipmentAssignment>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Assign_equipment_scope_saves_only_after_directory_validation()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();
        executions.Setup(r => r.TrySaveReleasedAssignmentAsync(
                It.IsAny<RecipeEquipmentAssignment>(), default))
            .ReturnsAsync(true);
        var directory = EquipmentDirectory();
        var service = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, directory.Object);

        var result = await service.AssignAsync(new RecipeAssignmentCommand(
            "A1", "EQ01", null, recipe.Id, recipe.Version), "operator");

        result.IsSuccess.Should().BeTrue();
        directory.Verify(x => x.GetEquipmentAsync("EQ01", default), Times.Once);
        executions.Verify(r => r.TrySaveReleasedAssignmentAsync(
            It.Is<RecipeEquipmentAssignment>(a => a.EquipmentId == "EQ01"), default), Times.Once);
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
                recipes.Object, new Mock<IRecipeParamRepository>().Object,
                executions.Object, EquipmentDirectory().Object);

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
        var command = Command(recipe);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();
        var directory = EquipmentDirectory();
        var service = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, directory.Object);

        var result = await service.RecordExecutionAsync(command, "operator-1");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("Released");
        executions.Verify(r => r.TryAddAssignedExecutionAsync(
            It.IsAny<RecipeExecutionSnapshot>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
                recipes.Object, parameters.Object, executions.Object, EquipmentDirectory().Object);

            var result = await service.RecordExecutionAsync(Command(recipe));

            result.IsFailure.Should().BeTrue();
            result.Error.Type.Should().Be(NexaOne.Common.ErrorType.Validation);
            executions.Verify(r => r.TryAddAssignedExecutionAsync(
                It.IsAny<RecipeExecutionSnapshot>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()), Times.Never);
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
        var command = Command(recipe);
        var assignment = AssignmentFor(recipe, command.AppliedAt);
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
        executions.Setup(r => r.GetEffectiveAssignmentAsync(
                command.EquipmentId, "WASHER", command.AppliedAt, default))
            .ReturnsAsync(assignment);
        executions.Setup(r => r.TryAddAssignedExecutionAsync(
                It.IsAny<RecipeExecutionSnapshot>(), assignment.AssignmentId, "WASHER", default))
            .Callback<RecipeExecutionSnapshot, string, string, CancellationToken>(
                (value, _, _, _) => saved = value)
            .ReturnsAsync(true);
        var directory = EquipmentDirectory();
        var service = new RecipeExecutionService(
            recipes.Object, parameters.Object, executions.Object, directory.Object);

        var result = await service.RecordExecutionAsync(command, "operator-1");

        result.IsSuccess.Should().BeTrue();
        saved.Should().NotBeNull();
        saved!.IdempotencyKey.Should().Be("idem-1");
        saved.AppliedBy.Should().Be("operator-1");
        using var header = JsonDocument.Parse(saved.RecipeSnapshotJson);
        header.RootElement.GetProperty("recipeId").GetString().Should().Be(recipe.Id);
        header.RootElement.GetProperty("version").GetInt32().Should().Be(recipe.Version);
        header.RootElement.GetProperty("assignmentId").GetString().Should().Be(assignment.AssignmentId);
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
        var directory = EquipmentDirectory();
        var assignment = AssignmentFor(recipe, command.AppliedAt);
        var service = new RecipeExecutionService(
            recipes.Object, parameters.Object, executions.Object, directory.Object);

        // First call establishes the canonical request hash.
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        parameters.Setup(r => r.GetByRecipeAsync(recipe.Id, default)).ReturnsAsync(Array.Empty<RecipeParam>());
        executions.Setup(r => r.GetExecutionByIdempotencyKeyAsync(command.IdempotencyKey, default))
            .ReturnsAsync((RecipeExecutionSnapshot?)null);
        executions.Setup(r => r.GetEffectiveAssignmentAsync(
                command.EquipmentId, "WASHER", command.AppliedAt, default))
            .ReturnsAsync(assignment);
        RecipeExecutionSnapshot? stored = null;
        executions.Setup(r => r.TryAddAssignedExecutionAsync(
                It.IsAny<RecipeExecutionSnapshot>(), assignment.AssignmentId, "WASHER", default))
            .Callback<RecipeExecutionSnapshot, string, string, CancellationToken>(
                (value, _, _, _) => stored = value)
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

    [Fact]
    public async Task RecordExecution_requires_the_effective_assignment_for_the_equipment()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var command = Command(recipe);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var parameters = new Mock<IRecipeParamRepository>();
        var executions = new Mock<IRecipeExecutionRepository>();
        executions.Setup(r => r.GetExecutionByIdempotencyKeyAsync(command.IdempotencyKey, default))
            .ReturnsAsync((RecipeExecutionSnapshot?)null);
        executions.Setup(r => r.GetEffectiveAssignmentAsync(
                command.EquipmentId, "WASHER", command.AppliedAt, default))
            .ReturnsAsync((RecipeEquipmentAssignment?)null);
        var directory = EquipmentDirectory();
        var service = new RecipeExecutionService(
            recipes.Object, parameters.Object, executions.Object, directory.Object);

        var result = await service.RecordExecutionAsync(command, "operator-1");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().ContainEquivalentOf("no effective recipe assignment");
        parameters.Verify(r => r.GetByRecipeAsync(
            It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        executions.Verify(r => r.TryAddAssignedExecutionAsync(
            It.IsAny<RecipeExecutionSnapshot>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RecordExecution_rejects_recipe_for_a_different_equipment_class()
    {
        var recipe = RecipeOf(RecipeApprovalState.Released);
        var command = Command(recipe);
        var recipes = new Mock<IRecipeRepository>();
        recipes.Setup(r => r.GetByIdAsync(recipe.Id, default)).ReturnsAsync(recipe);
        var executions = new Mock<IRecipeExecutionRepository>();
        executions.Setup(r => r.GetExecutionByIdempotencyKeyAsync(command.IdempotencyKey, default))
            .ReturnsAsync((RecipeExecutionSnapshot?)null);
        var directory = EquipmentDirectory(equipmentClassId: "ETCHER");
        var service = new RecipeExecutionService(
            recipes.Object, new Mock<IRecipeParamRepository>().Object,
            executions.Object, directory.Object);

        var result = await service.RecordExecutionAsync(command, "operator-1");

        result.IsFailure.Should().BeTrue();
        result.Error.Description.Should().Contain("WASHER").And.Contain("ETCHER");
        executions.Verify(r => r.GetEffectiveAssignmentAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTime>(),
            It.IsAny<CancellationToken>()), Times.Never);
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
