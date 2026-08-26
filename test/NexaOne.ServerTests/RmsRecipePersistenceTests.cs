using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using NexaOne.RMS.Infrastructure;
using NexaOne.ServiceContracts.Rms;
using NexusCom.Data.Abstractions.Interfaces;
using Xunit;

namespace NexaOne.ServerTests;

/// <summary>
/// RMS Recipe repository의 optional 복합 필터와 새 버전 header/parameter 원자 저장을 실제 SQLite로 검증한다.
/// </summary>
public sealed class RmsRecipePersistenceTests : IClassFixture<RmsRecipePersistenceTests.RecipeFactory>
{
    private readonly RecipeFactory _factory;

    public RmsRecipePersistenceTests(RecipeFactory factory) => _factory = factory;

    public sealed class RecipeFactory : WebApplicationFactory<Program>
    {
        public readonly string DbPath = Path.Combine(
            Path.GetTempPath(), $"nexaone-rms-recipe-{Guid.NewGuid():N}.db");

        public string ConnectionString => $"Data Source={DbPath};Foreign Keys=False";

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Server:Modules:Enabled", "false");
            builder.UseSetting("Database:Provider", "Sqlite");
            builder.UseSetting("ConnectionStrings:NexaOne", ConnectionString);
            builder.UseSetting("Jwt:SecretKey", "rms-recipe-e2e-jwt-secret-key-at-least-32bytes!!");
            builder.UseSetting("Jwt:Issuer", "nexaone-rms-recipe-test");
            builder.UseSetting("Jwt:Audience", "nexaone-rms-recipe-test");
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            try { if (File.Exists(DbPath)) File.Delete(DbPath); } catch { /* test cleanup */ }
        }
    }

    private (RecipeRepository Recipes, RecipeParamRepository Parameters) Repositories()
    {
        _ = _factory.CreateClient();
        var dataSource = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
        return (
            new RecipeRepository(dataSource, new ConfigurationBuilder().Build()),
            new RecipeParamRepository(dataSource));
    }

    private RecipeExecutionRepository ExecutionRepository()
    {
        _ = _factory.CreateClient();
        var dataSource = new EesDataSource
        {
            Provider = _factory.Services.GetRequiredService<IDatabaseProvider>(),
            ConnectionString = _factory.ConnectionString,
        };
        return new RecipeExecutionRepository(dataSource);
    }

    private static Recipe ReleasedRecipe(string id, string equipmentClassId)
    {
        var recipe = Recipe.Create(id, $"Recipe {id}", "test", equipmentClassId).Value;
        recipe.RequestApproval();
        recipe.Approve1("approver-1");
        recipe.Approve2("approver-2");
        recipe.Release("releaser");
        return recipe;
    }

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task List_supports_no_filter_and_equipment_class_plus_state()
    {
        var (recipes, _) = Repositories();
        var suffix = Suffix();
        var classA = $"CLASS_A_{suffix}";
        var classB = $"CLASS_B_{suffix}";
        var draftA = Recipe.Create($"R_DA_{suffix}", "Draft A", "test", classA).Value;
        var releasedA = ReleasedRecipe($"R_RA_{suffix}", classA);
        var releasedB = ReleasedRecipe($"R_RB_{suffix}", classB);
        await recipes.AddAsync(draftA);
        await recipes.AddAsync(releasedA);
        await recipes.AddAsync(releasedB);

        var all = await recipes.GetAsync(null, null);
        var combined = await recipes.GetAsync(classA, RecipeApprovalState.Released);

        all.Select(recipe => recipe.Id).Should().Contain([draftA.Id, releasedA.Id, releasedB.Id]);
        combined.Should().ContainSingle(recipe => recipe.Id == releasedA.Id);
    }

    [Fact]
    public async Task CreateNewVersion_copies_header_and_parameters()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var source = ReleasedRecipe($"R_SRC_{suffix}", $"CLASS_{suffix}");
        await recipes.AddAsync(source);
        var sourceParams = new[]
        {
            RecipeParam.Create($"P_TEMP_{suffix}", source.Id, "Temperature", "180", "C", 1).Value,
            RecipeParam.Create($"P_TIME_{suffix}", source.Id, "Duration", "30", "min", 2).Value,
        };
        foreach (var parameter in sourceParams)
            await parameters.AddAsync(parameter);
        var service = new RecipeService(recipes, parameters);
        var newRecipeId = $"R_NEW_{suffix}";

        var result = await service.CreateNewVersionAsync(source.Id, newRecipeId);

        result.IsSuccess.Should().BeTrue();
        var storedHeader = await recipes.GetByIdAsync(newRecipeId);
        var storedParams = await parameters.GetByRecipeAsync(newRecipeId);
        storedHeader.Should().NotBeNull();
        storedHeader!.ApprovalState.Should().Be(RecipeApprovalState.Draft);
        storedHeader.Version.Should().Be(source.Version + 1);
        storedParams.Should().HaveCount(2);
        storedParams.Select(parameter => parameter.Id)
            .Should().NotIntersectWith(sourceParams.Select(parameter => parameter.Id));
        storedParams.Select(parameter => (parameter.ParamName, parameter.ParamValue, parameter.Unit, parameter.SortOrder))
            .Should().Equal(sourceParams.Select(parameter =>
                (parameter.ParamName, parameter.ParamValue, parameter.Unit, parameter.SortOrder)));
    }

    [Fact]
    public async Task Failed_parameter_insert_rolls_back_new_version_header_and_all_parameters()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var source = ReleasedRecipe($"R_BASE_{suffix}", $"CLASS_{suffix}");
        var newVersion = source.CreateNewVersion($"R_FAIL_{suffix}");
        var duplicateParamId = $"P_DUP_{suffix}";
        var copiedParams = new[]
        {
            RecipeParam.Create(duplicateParamId, newVersion.Id, "A", "1", "u", 1).Value,
            RecipeParam.Create(duplicateParamId, newVersion.Id, "B", "2", "u", 2).Value,
        };

        var act = () => recipes.AddVersionAsync(newVersion, copiedParams);

        await act.Should().ThrowAsync<Exception>();
        (await recipes.GetByIdAsync(newVersion.Id)).Should().BeNull(
            "parameter INSERT 실패 시 header도 같은 트랜잭션에서 롤백되어야 한다");
        (await parameters.GetByRecipeAsync(newVersion.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Guarded_parameter_mutations_reject_a_recipe_released_after_a_stale_read()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create($"R_RACE_{suffix}", "Race", "test", $"CLASS_{suffix}").Value;
        var stored = RecipeParam.Create($"P_RACE_{suffix}", recipe.Id, "Temperature", "180", "C", 1).Value;
        await recipes.AddAsync(recipe);
        await parameters.AddAsync(stored);

        var staleParam = await parameters.GetByIdAsync(stored.Id);
        recipe.RequestApproval();
        recipe.Approve1("approver-1");
        recipe.Approve2("approver-2");
        recipe.Release("releaser");
        await recipes.UpdateAsync(recipe);

        var lateAdd = RecipeParam.Create($"P_LATE_{suffix}", recipe.Id, "Pressure", "2", "bar", 2).Value;
        staleParam!.UpdateValue("190");
        (await parameters.TryAddIfRecipeEditableAsync(lateAdd)).Should().BeFalse();
        (await parameters.TryUpdateIfRecipeEditableAsync(staleParam)).Should().BeFalse();
        (await parameters.TryDeleteIfRecipeEditableAsync(stored.Id)).Should().BeFalse();

        (await parameters.GetByIdAsync(lateAdd.Id)).Should().BeNull();
        (await parameters.GetByIdAsync(stored.Id))!.ParamValue.Should().Be("180");
    }

    [Fact]
    public async Task Assignment_supports_equipment_and_equipment_class_and_replaces_the_active_target()
    {
        var (recipes, parameters) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var equipmentId = $"EQ_{suffix}";
        var classId = $"CLASS_{suffix}";
        var recipe = ReleasedRecipe($"R_ASG_{suffix}", classId);
        await recipes.AddAsync(recipe);
        var service = new RecipeExecutionService(recipes, parameters, executions);

        var first = await service.AssignAsync(new RecipeAssignmentCommand(
            $"A_EQ1_{suffix}", equipmentId, null, recipe.Id, recipe.Version), "operator-1");
        var second = await service.AssignAsync(new RecipeAssignmentCommand(
            $"A_EQ2_{suffix}", equipmentId, null, recipe.Id, recipe.Version), "operator-2");
        var byClass = await service.AssignAsync(new RecipeAssignmentCommand(
            $"A_CLASS_{suffix}", null, classId, recipe.Id, recipe.Version), "engineer-1");

        first.IsSuccess.Should().BeTrue();
        second.IsSuccess.Should().BeTrue();
        byClass.IsSuccess.Should().BeTrue();
        var activeEquipment = await executions.GetAssignmentsAsync(equipmentId, null, true);
        activeEquipment.Should().ContainSingle(row =>
            row.AssignmentId == second.Value.AssignmentId && row.AssignedBy == "operator-2");
        var equipmentHistory = await executions.GetAssignmentsAsync(equipmentId, null, false);
        equipmentHistory.Should().HaveCount(2);
        equipmentHistory.Should().ContainSingle(row =>
            row.AssignmentId == first.Value.AssignmentId && !row.IsActive && row.EffectiveTo.HasValue);
        (await executions.GetAssignmentsAsync(null, classId, true))
            .Should().ContainSingle(row => row.AssignmentId == byClass.Value.AssignmentId);
    }

    [Fact]
    public async Task Repository_guard_rejects_draft_without_replacing_the_active_assignment()
    {
        var (recipes, _) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var equipmentId = $"EQ_GUARD_{suffix}";
        var classId = $"CLASS_GUARD_{suffix}";
        var released = ReleasedRecipe($"R_RELEASED_{suffix}", classId);
        var draft = Recipe.Create($"R_DRAFT_{suffix}", "Draft", "test", classId).Value;
        await recipes.AddAsync(released);
        await recipes.AddAsync(draft);
        var active = new RecipeEquipmentAssignment(
            $"A_RELEASED_{suffix}", equipmentId, null, released.Id, released.Version,
            DateTime.UtcNow.AddMinutes(-1), null, "operator-1", true);
        var rejected = new RecipeEquipmentAssignment(
            $"A_DRAFT_{suffix}", equipmentId, null, draft.Id, draft.Version,
            DateTime.UtcNow, null, "operator-2", true);
        (await executions.TrySaveReleasedAssignmentAsync(active)).Should().BeTrue();

        (await executions.TrySaveReleasedAssignmentAsync(rejected)).Should().BeFalse();

        (await executions.GetAssignmentsAsync(equipmentId, null, true))
            .Should().ContainSingle(row => row.AssignmentId == active.AssignmentId);
        (await executions.GetAssignmentsAsync(equipmentId, null, false))
            .Should().NotContain(row => row.AssignmentId == rejected.AssignmentId);
    }

    [Fact]
    public async Task Released_execution_snapshot_is_immutable_and_idempotent_in_sqlite()
    {
        var (recipes, parameters) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var recipe = ReleasedRecipe($"R_EXE_{suffix}", $"CLASS_{suffix}");
        await recipes.AddAsync(recipe);
        await parameters.AddAsync(RecipeParam.Create(
            $"P_EXE_{suffix}", recipe.Id, "Temperature", "180", "C", 1).Value);
        var service = new RecipeExecutionService(recipes, parameters, executions);
        var command = new RecipeExecutionCommand(
            $"EXE_{suffix}", $"IDEM_{suffix}", "PLANT01", $"EQ_{suffix}",
            recipe.Id, recipe.Version,
            new DateTime(2026, 8, 26, 5, 0, 0, DateTimeKind.Utc), "Equipment",
            ProcessLotId: $"LOT_{suffix}", WorkOrderId: $"WO_{suffix}",
            TraceId: $"TRACE_{suffix}", ConditionSnapshotJson: "{\"pressure\":2.1}");

        var created = await service.RecordExecutionAsync(command, "operator-1");
        var replay = await service.RecordExecutionAsync(command, "operator-1");
        var stored = await executions.GetExecutionAsync(command.ExecutionId);

        created.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        stored.Should().NotBeNull();
        stored!.AppliedBy.Should().Be("operator-1");
        stored.IdempotencyKey.Should().Be(command.IdempotencyKey);
        stored.RecipeSnapshotJson.Should().Contain($"\"recipeId\":\"{recipe.Id}\"");
        stored.RecipeSnapshotJson.Should().Contain($"\"version\":{recipe.Version}");
        stored.ParameterSnapshotJson.Should().Contain("Temperature");
        stored.ConditionSnapshotJson.Should().Be("{\"pressure\":2.1}");
        (await executions.GetExecutionByIdempotencyKeyAsync(command.IdempotencyKey))!
            .ExecutionId.Should().Be(command.ExecutionId);
    }

    [Fact]
    public async Task Repository_guard_prevents_snapshot_for_a_draft_recipe()
    {
        var (recipes, _) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var recipe = Recipe.Create($"R_DRAFT_EXE_{suffix}", "Draft", "test", $"CLASS_{suffix}").Value;
        await recipes.AddAsync(recipe);
        var snapshot = new RecipeExecutionSnapshot(
            $"EXE_DRAFT_{suffix}", $"IDEM_DRAFT_{suffix}", new string('A', 64),
            "PLANT01", $"EQ_{suffix}", null, null, null, recipe.Id, recipe.Version,
            "{}", "[]", null, "operator-1", DateTime.UtcNow, "Equipment", null, DateTime.UtcNow);

        (await executions.TryAddExecutionAsync(snapshot)).Should().BeFalse();
        (await executions.GetExecutionAsync(snapshot.ExecutionId)).Should().BeNull();
    }
}
