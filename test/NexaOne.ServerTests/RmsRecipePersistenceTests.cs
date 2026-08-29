using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NexaOne.Common;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;
using NexaOne.RMS.Infrastructure;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Rms;
using NexaDB.Data.Abstractions.Interfaces;
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

    private static async Task AddRecipeAsync(RecipeRepository repository, Recipe recipe)
    {
        var write = new RecipeWriteRecord(
            $"TEST_RWC_{Guid.NewGuid():N}", "Create", $"test:recipe:{Guid.NewGuid():N}",
            new string('A', 64), recipe.Id, null, "test-seed", DateTime.UtcNow);
        (await repository.TryAddAsync(recipe, write)).Should().BeTrue();
    }

    private static async Task AddParameterAsync(
        RecipeParamRepository repository, RecipeParam parameter)
    {
        var write = new RecipeParamWriteRecord(
            $"TEST_RPC_{Guid.NewGuid():N}", "Add", $"test:param:{Guid.NewGuid():N}",
            new string('B', 64), parameter.Id, parameter.RecipeId, parameter.ParamName,
            parameter.ParamValue, parameter.Unit, parameter.SortOrder, null,
            parameter.Version, "test-seed", DateTime.UtcNow);
        (await repository.TryAddAsync(parameter, write)).Should().BeTrue();
    }

    private static async Task ReleaseRecipeAsync(RecipeRepository repository, Recipe recipe)
    {
        async Task TransitionAsync(
            RecipeApprovalState expected, Func<Result> transition, string actor)
        {
            transition().IsSuccess.Should().BeTrue();
            (await repository.TryTransitionAsync(
                recipe, expected,
                new RecipeTransitionWrite(
                    $"test:transition:{Guid.NewGuid():N}", new string('C', 64), actor, null)))
                .Should().BeTrue();
        }

        await TransitionAsync(RecipeApprovalState.Draft, recipe.RequestApproval, "requester");
        await TransitionAsync(RecipeApprovalState.WaitApproval, () => recipe.Approve1("approver-1"), "approver-1");
        await TransitionAsync(RecipeApprovalState.Approved1, () => recipe.Approve2("approver-2"), "approver-2");
        await TransitionAsync(RecipeApprovalState.Approved, () => recipe.Release("releaser"), "releaser");
    }

    private void SeedEquipment(string equipmentId, string equipmentClassId, string plantId = "PLANT01")
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO MDM_EQUIPMENT
              (EQUIPMENT_ID, EQUIPMENT_NAME, PLANT_ID, AREA_ID, EQUIPMENT_TYPE,
               EQUIPMENT_CLASS_ID, VALID_STATE, CREATED_BY, UPDATED_BY)
            VALUES (@equipmentId, @equipmentId, @plantId, 'AREA_TEST', 'TEST',
                    @equipmentClassId, 'Valid', 'TEST', 'TEST');
            """;
        command.Parameters.AddWithValue("@equipmentId", equipmentId);
        command.Parameters.AddWithValue("@equipmentClassId", equipmentClassId);
        command.Parameters.AddWithValue("@plantId", plantId);
        command.ExecuteNonQuery();
    }

    private static RecipeExecutionService ExecutionService(
        IRecipeRepository recipes,
        IRecipeParamRepository parameters,
        IRecipeExecutionRepository executions,
        string equipmentId,
        string equipmentClassId,
        string plantId = "PLANT01")
        => new(recipes, parameters, executions,
            new FixedEquipmentDirectory(new EquipmentDirectoryEntry(
                equipmentId, plantId, equipmentClassId, true)));

    private sealed class FixedEquipmentDirectory(EquipmentDirectoryEntry equipment)
        : IEquipmentDirectory
    {
        public Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(
            string plantId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                equipment.PlantId.Equals(plantId, StringComparison.OrdinalIgnoreCase)
                    ? [equipment.EquipmentId]
                    : []);

        public Task<EquipmentDirectoryEntry?> GetEquipmentAsync(
            string equipmentId,
            CancellationToken ct = default)
            => Task.FromResult<EquipmentDirectoryEntry?>(
                equipment.EquipmentId.Equals(equipmentId, StringComparison.OrdinalIgnoreCase)
                    ? equipment
                    : null);

        public Task<bool> EquipmentClassExistsAsync(
            string equipmentClassId,
            CancellationToken ct = default)
            => Task.FromResult(
                equipment.EquipmentClassId.Equals(
                    equipmentClassId,
                    StringComparison.OrdinalIgnoreCase));
    }

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
        await AddRecipeAsync(recipes, draftA);
        await AddRecipeAsync(recipes, releasedA);
        await AddRecipeAsync(recipes, releasedB);

        var all = await recipes.GetAsync(null, null);
        var combined = await recipes.GetAsync(classA, RecipeApprovalState.Released);

        all.Select(recipe => recipe.Id).Should().Contain([draftA.Id, releasedA.Id, releasedB.Id]);
        combined.Should().ContainSingle(recipe => recipe.Id == releasedA.Id);
    }

    [Fact]
    public async Task Approval_command_replays_exact_retry_and_rejects_changed_payload_for_the_same_key()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create(
            $"R_APPROVAL_{suffix}", "Approval", "test", $"CLASS_{suffix}").Value;
        await AddRecipeAsync(recipes, recipe);
        var service = new RecipeService(recipes, parameters);
        var context = new RecipeCommandContext("requester-1", $"approval:{suffix}");

        var first = await service.RequestApprovalAsync(recipe.Id, context);
        var replay = await service.RequestApprovalAsync(recipe.Id, context);
        var changedActor = await service.RequestApprovalAsync(
            recipe.Id, context with { ActorId = "requester-2" });

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue(
            "an exact retry must replay the committed transition even though the recipe state advanced");
        changedActor.IsFailure.Should().BeTrue();
        changedActor.Error.Code.Should().Be("RMS.Recipe.IdempotencyConflict");
        var history = await recipes.GetApprovalHistoryAsync(recipe.Id);
        history.Should().ContainSingle();
        history[0].IdempotencyKey.Should().Be(context.IdempotencyKey);
        history[0].RequestHash.Should().MatchRegex("^[0-9A-F]{64}$");
    }

    [Fact]
    public async Task Recipe_create_and_parameter_add_delete_replay_exact_requests_and_reject_key_reuse()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var service = new RecipeService(recipes, parameters);
        var recipeId = $"R_COMMAND_{suffix}";
        var create = new RecipeCreateCommand(
            recipeId, "Command integrity", "test", $"CLASS_{suffix}",
            $"recipe-create:{suffix}", "engineer-1");

        var created = await service.CreateRecipeAsync(create);
        var createReplay = await service.CreateRecipeAsync(create);
        var createConflict = await service.CreateRecipeAsync(create with { Name = "Changed" });

        created.IsSuccess.Should().BeTrue();
        createReplay.IsSuccess.Should().BeTrue();
        createReplay.Value.Id.Should().Be(recipeId);
        createConflict.IsFailure.Should().BeTrue();
        createConflict.Error.Code.Should().Be("RMS.Recipe.IdempotencyConflict");

        var add = new RecipeParamAddCommand(
            $"P_COMMAND_{suffix}", recipeId, "Temperature", "180", "C", 1,
            $"param-add:{suffix}", "engineer-1");
        var added = await service.AddParamAsync(add);
        var addReplay = await service.AddParamAsync(add);
        var addConflict = await service.AddParamAsync(add with { ParamValue = "190" });

        added.IsSuccess.Should().BeTrue();
        addReplay.IsSuccess.Should().BeTrue();
        addReplay.Value.ParamValue.Should().Be("180");
        addConflict.IsFailure.Should().BeTrue();
        addConflict.Error.Code.Should().Be("RMS.RecipeParam.IdempotencyConflict");

        var delete = new RecipeParamDeleteCommand(
            add.ParamId, 1, $"param-delete:{suffix}", "engineer-1");
        var deleted = await service.DeleteParamAsync(delete);
        var deleteReplay = await service.DeleteParamAsync(delete);
        var deleteConflict = await service.DeleteParamAsync(delete with { ActorId = "engineer-2" });

        deleted.IsSuccess.Should().BeTrue();
        deleteReplay.IsSuccess.Should().BeTrue(
            "the immutable ledger must replay deletion after the parameter row is gone");
        deleteConflict.IsFailure.Should().BeTrue();
        deleteConflict.Error.Code.Should().Be("RMS.RecipeParam.IdempotencyConflict");
        (await parameters.GetByIdAsync(add.ParamId)).Should().BeNull();

        var writes = new[]
        {
            await parameters.GetWriteByIdempotencyKeyAsync(add.IdempotencyKey),
            await parameters.GetWriteByIdempotencyKeyAsync(delete.IdempotencyKey),
        };
        writes.Should().OnlyContain(write => write != null);
        writes.Select(write => write!.CommandType).Should().Equal("Add", "Delete");
        writes.Should().OnlyContain(write => write!.ChangedBy == "engineer-1");
    }

    [Fact]
    public async Task Parallel_approval_retries_commit_one_transition_history_row()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create(
            $"R_APPROVAL_RACE_{suffix}", "Approval race", "test", $"CLASS_{suffix}").Value;
        await AddRecipeAsync(recipes, recipe);
        var command = new RecipeCommandContext("requester-1", $"approval-race:{suffix}");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var (attemptRecipes, attemptParameters) = Repositories();
            return await new RecipeService(attemptRecipes, attemptParameters)
                .RequestApprovalAsync(recipe.Id, command);
        }));

        results.Should().OnlyContain(result => result.IsSuccess);
        (await recipes.GetApprovalHistoryAsync(recipe.Id)).Should().ContainSingle();
        (await recipes.GetByIdAsync(recipe.Id))!.ApprovalState
            .Should().Be(RecipeApprovalState.WaitApproval);
    }

    [Fact]
    public async Task Concurrent_approval_commands_with_different_keys_have_one_CAS_winner()
    {
        var (recipes, _) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create(
            $"R_APPROVAL_CAS_{suffix}", "Approval CAS", "test", $"CLASS_{suffix}").Value;
        await AddRecipeAsync(recipes, recipe);
        var secondRepositories = Repositories();
        var firstStale = await recipes.GetByIdAsync(recipe.Id);
        var secondStale = await secondRepositories.Recipes.GetByIdAsync(recipe.Id);
        firstStale!.RequestApproval();
        secondStale!.RequestApproval();

        var attempts = await Task.WhenAll(
            recipes.TryTransitionAsync(
                firstStale, RecipeApprovalState.Draft,
                new RecipeTransitionWrite(
                    $"approval-cas:a:{suffix}", new string('A', 64), "requester-1", null)),
            secondRepositories.Recipes.TryTransitionAsync(
                secondStale, RecipeApprovalState.Draft,
                new RecipeTransitionWrite(
                    $"approval-cas:b:{suffix}", new string('B', 64), "requester-1", null)));

        attempts.Count(result => result).Should().Be(1);
        attempts.Count(result => !result).Should().Be(1);
        (await recipes.GetApprovalHistoryAsync(recipe.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task Parameter_update_uses_version_CAS_and_replays_only_the_same_canonical_request()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create(
            $"R_PARAM_{suffix}", "Parameter", "test", $"CLASS_{suffix}").Value;
        var parameter = RecipeParam.Create(
            $"P_PARAM_{suffix}", recipe.Id, "Temperature", "180", "C", 1).Value;
        await AddRecipeAsync(recipes, recipe);
        await AddParameterAsync(parameters, parameter);
        var service = new RecipeService(recipes, parameters);
        var command = new RecipeParamUpdateCommand(
            parameter.Id, "190", 1, $"param-update:{suffix}", "engineer-1");

        var first = await service.UpdateParamAsync(command);
        var replay = await service.UpdateParamAsync(command);
        var changedPayload = await service.UpdateParamAsync(command with { NewValue = "200" });
        var staleVersion = await service.UpdateParamAsync(command with
        {
            IdempotencyKey = $"param-stale:{suffix}",
            ExpectedVersion = 1,
        });

        first.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        changedPayload.IsFailure.Should().BeTrue();
        changedPayload.Error.Code.Should().Be("RMS.RecipeParam.IdempotencyConflict");
        staleVersion.IsFailure.Should().BeTrue();
        staleVersion.Error.Code.Should().Be("RMS.RecipeParam.ConcurrentUpdate");
        var stored = await parameters.GetByIdAsync(parameter.Id);
        stored!.ParamValue.Should().Be("190");
        stored.Version.Should().Be(2);
        (await parameters.GetWriteByIdempotencyKeyAsync(command.IdempotencyKey))
            .Should().NotBeNull();
    }

    [Fact]
    public async Task Parallel_parameter_retries_increment_the_version_once()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create(
            $"R_PARAM_RACE_{suffix}", "Parameter race", "test", $"CLASS_{suffix}").Value;
        var parameter = RecipeParam.Create(
            $"P_PARAM_RACE_{suffix}", recipe.Id, "Pressure", "2", "bar", 1).Value;
        await AddRecipeAsync(recipes, recipe);
        await AddParameterAsync(parameters, parameter);
        var command = new RecipeParamUpdateCommand(
            parameter.Id, "2.5", 1, $"param-race:{suffix}", "engineer-1");

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var (attemptRecipes, attemptParameters) = Repositories();
            return await new RecipeService(attemptRecipes, attemptParameters)
                .UpdateParamAsync(command);
        }));

        results.Should().OnlyContain(result => result.IsSuccess);
        var stored = await parameters.GetByIdAsync(parameter.Id);
        stored!.ParamValue.Should().Be("2.5");
        stored.Version.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_parameter_updates_with_different_keys_have_one_version_CAS_winner()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create(
            $"R_PARAM_CAS_{suffix}", "Parameter CAS", "test", $"CLASS_{suffix}").Value;
        var parameter = RecipeParam.Create(
            $"P_PARAM_CAS_{suffix}", recipe.Id, "Flow", "10", "L/min", 1).Value;
        await AddRecipeAsync(recipes, recipe);
        await AddParameterAsync(parameters, parameter);
        var firstRepositories = Repositories();
        var secondRepositories = Repositories();

        var attempts = await Task.WhenAll(
            new RecipeService(firstRepositories.Recipes, firstRepositories.Parameters).UpdateParamAsync(
                new RecipeParamUpdateCommand(
                    parameter.Id, "11", 1, $"param-cas:a:{suffix}", "engineer-1")),
            new RecipeService(secondRepositories.Recipes, secondRepositories.Parameters).UpdateParamAsync(
                new RecipeParamUpdateCommand(
                    parameter.Id, "12", 1, $"param-cas:b:{suffix}", "engineer-2")));

        attempts.Count(result => result.IsSuccess).Should().Be(1);
        attempts.Count(result => result.IsFailure
            && result.Error.Code == "RMS.RecipeParam.ConcurrentUpdate").Should().Be(1);
        var stored = await parameters.GetByIdAsync(parameter.Id);
        stored!.Version.Should().Be(2);
        stored.ParamValue.Should().BeOneOf("11", "12");
    }

    [Fact]
    public async Task Parameter_command_ledger_is_append_only_even_when_recursive_triggers_are_off()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create(
            $"R_PARAM_LEDGER_{suffix}", "Parameter ledger", "test", $"CLASS_{suffix}").Value;
        var parameter = RecipeParam.Create(
            $"P_PARAM_LEDGER_{suffix}", recipe.Id, "Speed", "100", "rpm", 1).Value;
        await AddRecipeAsync(recipes, recipe);
        await AddParameterAsync(parameters, parameter);
        var key = $"param-ledger:{suffix}";
        var updated = await new RecipeService(recipes, parameters).UpdateParamAsync(
            new RecipeParamUpdateCommand(parameter.Id, "110", 1, key, "engineer-1"));
        updated.IsSuccess.Should().BeTrue();

        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(_factory.ConnectionString);
        connection.Open();
        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA recursive_triggers = OFF;";
            pragma.ExecuteNonQuery();
        }

        Action update = () => ExecuteLedgerMutation(
            connection,
            "UPDATE RMS_RECIPE_PARAM_COMMAND SET PARAM_VALUE='tampered' WHERE IDEMPOTENCY_KEY=@key",
            key);
        Action delete = () => ExecuteLedgerMutation(
            connection,
            "DELETE FROM RMS_RECIPE_PARAM_COMMAND WHERE IDEMPOTENCY_KEY=@key",
            key);
        Action replace = () => ExecuteLedgerMutation(
            connection,
            "INSERT OR REPLACE INTO RMS_RECIPE_PARAM_COMMAND SELECT * FROM RMS_RECIPE_PARAM_COMMAND WHERE IDEMPOTENCY_KEY=@key",
            key);

        update.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
        delete.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
        replace.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM RMS_RECIPE_PARAM_COMMAND WHERE IDEMPOTENCY_KEY=@key";
        count.Parameters.AddWithValue("@key", key);
        Convert.ToInt64(count.ExecuteScalar()).Should().Be(1);
    }

    private static void ExecuteLedgerMutation(
        Microsoft.Data.Sqlite.SqliteConnection connection, string sql, string key)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@key", key);
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task CreateNewVersion_copies_header_and_parameters()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var source = Recipe.Create(
            $"R_SRC_{suffix}", $"Recipe R_SRC_{suffix}", "test", $"CLASS_{suffix}").Value;
        await AddRecipeAsync(recipes, source);
        var sourceParams = new[]
        {
            RecipeParam.Create($"P_TEMP_{suffix}", source.Id, "Temperature", "180", "C", 1).Value,
            RecipeParam.Create($"P_TIME_{suffix}", source.Id, "Duration", "30", "min", 2).Value,
        };
        foreach (var parameter in sourceParams)
            await AddParameterAsync(parameters, parameter);
        await ReleaseRecipeAsync(recipes, source);
        var service = new RecipeService(recipes, parameters);
        var newRecipeId = $"R_NEW_{suffix}";

        var command = new RecipeVersionCreateCommand(
            source.Id, newRecipeId, $"version:{suffix}", "engineer-1");
        var result = await service.CreateNewVersionAsync(command);
        var replay = await service.CreateNewVersionAsync(command);
        var conflict = await service.CreateNewVersionAsync(command with
        {
            NewRecipeId = $"R_OTHER_{suffix}",
        });

        result.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.Id.Should().Be(newRecipeId);
        conflict.IsFailure.Should().BeTrue();
        conflict.Error.Code.Should().Be("RMS.Recipe.IdempotencyConflict");
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
        var write = await recipes.GetWriteByIdempotencyKeyAsync(command.IdempotencyKey);
        write.Should().NotBeNull();
        write!.CommandType.Should().Be("CreateVersion");
        write.ActorId.Should().Be("engineer-1");
    }

    [Fact]
    public async Task Failed_parameter_insert_rolls_back_new_version_header_and_all_parameters()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var source = ReleasedRecipe($"R_BASE_{suffix}", $"CLASS_{suffix}");
        var newVersion = source.CreateNewVersion($"R_FAIL_{suffix}").Value;
        var duplicateParamId = $"P_DUP_{suffix}";
        var copiedParams = new[]
        {
            RecipeParam.Create(duplicateParamId, newVersion.Id, "A", "1", "u", 1).Value,
            RecipeParam.Create(duplicateParamId, newVersion.Id, "B", "2", "u", 2).Value,
        };

        var write = new RecipeWriteRecord(
            $"RWC_{Guid.NewGuid():N}", "CreateVersion", $"version-fail:{suffix}",
            new string('C', 64), newVersion.Id, source.Id, "engineer-1", DateTime.UtcNow);
        var act = () => recipes.TryAddVersionAsync(newVersion, copiedParams, write);

        await act.Should().ThrowAsync<Exception>();
        (await recipes.GetByIdAsync(newVersion.Id)).Should().BeNull(
            "parameter INSERT 실패 시 header도 같은 트랜잭션에서 롤백되어야 한다");
        (await parameters.GetByRecipeAsync(newVersion.Id)).Should().BeEmpty();
    }

    [Fact]
    public async Task Guarded_parameter_mutations_reject_a_recipe_that_left_draft_after_a_stale_read()
    {
        var (recipes, parameters) = Repositories();
        var suffix = Suffix();
        var recipe = Recipe.Create($"R_RACE_{suffix}", "Race", "test", $"CLASS_{suffix}").Value;
        var stored = RecipeParam.Create($"P_RACE_{suffix}", recipe.Id, "Temperature", "180", "C", 1).Value;
        await AddRecipeAsync(recipes, recipe);
        await AddParameterAsync(parameters, stored);

        var staleParam = await parameters.GetByIdAsync(stored.Id);
        recipe.RequestApproval();
        (await recipes.TryTransitionAsync(
            recipe, RecipeApprovalState.Draft,
            new RecipeTransitionWrite(
                $"approval-race-guard:{suffix}", new string('A', 64), "race-test", null)))
            .Should().BeTrue();

        var lateAdd = RecipeParam.Create($"P_LATE_{suffix}", recipe.Id, "Pressure", "2", "bar", 2).Value;
        var lateAddWrite = new RecipeParamWriteRecord(
            $"RPC_ADD_{suffix}", "Add", $"param-add-race-guard:{suffix}", new string('B', 64),
            lateAdd.Id, lateAdd.RecipeId, lateAdd.ParamName, lateAdd.ParamValue, lateAdd.Unit,
            lateAdd.SortOrder, null, 1, "race-test", DateTime.UtcNow);
        var lateUpdate = new RecipeParamWriteRecord(
            $"RPC_UPD_{suffix}", "Update", $"param-race-guard:{suffix}", new string('C', 64),
            staleParam!.Id, staleParam.RecipeId, staleParam.ParamName, "190", staleParam.Unit,
            staleParam.SortOrder, staleParam.Version, staleParam.Version + 1,
            "race-test", DateTime.UtcNow);
        var lateDelete = new RecipeParamWriteRecord(
            $"RPC_DEL_{suffix}", "Delete", $"param-delete-race-guard:{suffix}", new string('D', 64),
            staleParam.Id, staleParam.RecipeId, staleParam.ParamName, staleParam.ParamValue,
            staleParam.Unit, staleParam.SortOrder, staleParam.Version, staleParam.Version,
            "race-test", DateTime.UtcNow);
        (await parameters.TryAddAsync(lateAdd, lateAddWrite)).Should().BeFalse();
        (await parameters.TryUpdateAsync(lateUpdate)).Should().BeFalse();
        (await parameters.TryDeleteAsync(lateDelete)).Should().BeFalse();

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
        await AddRecipeAsync(recipes, recipe);
        SeedEquipment(equipmentId, classId);
        var service = ExecutionService(recipes, parameters, executions, equipmentId, classId);

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
        await AddRecipeAsync(recipes, released);
        await AddRecipeAsync(recipes, draft);
        SeedEquipment(equipmentId, classId);
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
    public async Task Future_assignment_is_rejected_without_replacing_the_current_assignment()
    {
        var (recipes, parameters) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var equipmentId = $"EQ_FUTURE_{suffix}";
        var classId = $"CLASS_FUTURE_{suffix}";
        var recipe = ReleasedRecipe($"R_FUTURE_{suffix}", classId);
        await AddRecipeAsync(recipes, recipe);
        SeedEquipment(equipmentId, classId);
        var service = ExecutionService(recipes, parameters, executions, equipmentId, classId);
        var current = await service.AssignAsync(new RecipeAssignmentCommand(
            $"A_CURRENT_{suffix}", equipmentId, null, recipe.Id, recipe.Version,
            DateTime.UtcNow.AddMinutes(-1)), "operator-1");

        var rejectedByService = await service.AssignAsync(new RecipeAssignmentCommand(
            $"A_FUTURE_SERVICE_{suffix}", equipmentId, null, recipe.Id, recipe.Version,
            DateTime.UtcNow.AddHours(1)), "operator-2");
        var rejectedByRepository = await executions.TrySaveReleasedAssignmentAsync(
            new RecipeEquipmentAssignment(
                $"A_FUTURE_REPO_{suffix}", equipmentId, null, recipe.Id, recipe.Version,
                DateTime.UtcNow.AddHours(1), null, "operator-3", true));

        current.IsSuccess.Should().BeTrue();
        rejectedByService.IsFailure.Should().BeTrue();
        rejectedByService.Error.Description.Should().ContainEquivalentOf("future");
        rejectedByRepository.Should().BeFalse();
        (await executions.GetAssignmentsAsync(equipmentId, null, true))
            .Should().ContainSingle(row => row.AssignmentId == current.Value.AssignmentId);
    }

    [Fact]
    public async Task Execution_uses_the_assignment_effective_at_applied_time_and_sql_guard_blocks_bypass()
    {
        var (recipes, parameters) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var equipmentId = $"EQ_PERIOD_{suffix}";
        var classId = $"CLASS_PERIOD_{suffix}";
        var firstRecipe = ReleasedRecipe($"R_PERIOD_1_{suffix}", classId);
        var secondRecipe = ReleasedRecipe($"R_PERIOD_2_{suffix}", classId);
        await AddRecipeAsync(recipes, firstRecipe);
        await AddRecipeAsync(recipes, secondRecipe);
        SeedEquipment(equipmentId, classId);

        var directory = new FixedEquipmentDirectory(new EquipmentDirectoryEntry(
            equipmentId, "PLANT01", classId, true));
        var service = new RecipeExecutionService(recipes, parameters, executions, directory);
        var firstFrom = DateTime.UtcNow.AddMinutes(-10);
        var secondFrom = DateTime.UtcNow.AddMinutes(-5);
        var firstAssignmentId = $"A_PERIOD_1_{suffix}";
        var secondAssignmentId = $"A_PERIOD_2_{suffix}";
        var firstAssignment = await service.AssignAsync(new RecipeAssignmentCommand(
            firstAssignmentId, equipmentId, null,
            firstRecipe.Id, firstRecipe.Version, firstFrom), "engineer-1");
        var secondAssignment = await service.AssignAsync(new RecipeAssignmentCommand(
            secondAssignmentId, equipmentId, null,
            secondRecipe.Id, secondRecipe.Version, secondFrom), "engineer-2");

        var historicalCommand = new RecipeExecutionCommand(
            $"EXE_PERIOD_OLD_{suffix}", $"IDEM_PERIOD_OLD_{suffix}",
            "PLANT01", equipmentId, firstRecipe.Id, firstRecipe.Version,
            firstFrom.AddMinutes(1), "Equipment");
        var staleCommand = new RecipeExecutionCommand(
            $"EXE_PERIOD_STALE_{suffix}", $"IDEM_PERIOD_STALE_{suffix}",
            "PLANT01", equipmentId, firstRecipe.Id, firstRecipe.Version,
            secondFrom.AddMinutes(1), "Equipment");
        var currentCommand = new RecipeExecutionCommand(
            $"EXE_PERIOD_NEW_{suffix}", $"IDEM_PERIOD_NEW_{suffix}",
            "PLANT01", equipmentId, secondRecipe.Id, secondRecipe.Version,
            secondFrom.AddMinutes(1), "Equipment");

        var historical = await service.RecordExecutionAsync(historicalCommand, "operator-1");
        var stale = await service.RecordExecutionAsync(staleCommand, "operator-1");
        var current = await service.RecordExecutionAsync(currentCommand, "operator-1");

        var bypass = new RecipeExecutionSnapshot(
            $"EXE_BYPASS_{suffix}", $"IDEM_BYPASS_{suffix}", new string('B', 64),
            "PLANT01", equipmentId, null, null, null,
            firstRecipe.Id, firstRecipe.Version, "{}", "[]", null,
            "operator-1", secondFrom.AddMinutes(1), "Equipment", null, DateTime.UtcNow);
        var bypassed = await executions.TryAddAssignedExecutionAsync(
            bypass, firstAssignmentId, classId);

        firstAssignment.IsSuccess.Should().BeTrue();
        secondAssignment.IsSuccess.Should().BeTrue();
        historical.IsSuccess.Should().BeTrue(
            "a closed historical assignment is still authoritative inside its effective period");
        stale.IsFailure.Should().BeTrue();
        stale.Error.Description.Should().ContainEquivalentOf("selects recipe");
        current.IsSuccess.Should().BeTrue();
        bypassed.Should().BeFalse(
            "the repository must reject an assignment outside the execution's AppliedAt period");
        (await executions.GetExecutionAsync(bypass.ExecutionId)).Should().BeNull();
    }

    [Fact]
    public async Task Released_execution_snapshot_is_immutable_and_idempotent_in_sqlite()
    {
        var (recipes, parameters) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var equipmentId = $"EQ_{suffix}";
        var classId = $"CLASS_{suffix}";
        var recipe = Recipe.Create($"R_EXE_{suffix}", $"Recipe R_EXE_{suffix}", "test", classId).Value;
        await AddRecipeAsync(recipes, recipe);
        await AddParameterAsync(parameters, RecipeParam.Create(
            $"P_EXE_{suffix}", recipe.Id, "Temperature", "180", "C", 1).Value);
        await ReleaseRecipeAsync(recipes, recipe);
        SeedEquipment(equipmentId, classId);
        var directory = new FixedEquipmentDirectory(new EquipmentDirectoryEntry(
            equipmentId, "PLANT01", classId, true));
        var service = new RecipeExecutionService(recipes, parameters, executions, directory);
        var appliedAt = new DateTime(2026, 8, 26, 5, 0, 0, DateTimeKind.Utc);
        var assigned = await service.AssignAsync(new RecipeAssignmentCommand(
            $"ASG_{suffix}", equipmentId, null, recipe.Id, recipe.Version,
            appliedAt.AddMinutes(-1)), "engineer-1");
        var command = new RecipeExecutionCommand(
            $"EXE_{suffix}", $"IDEM_{suffix}", "PLANT01", equipmentId,
            recipe.Id, recipe.Version,
            appliedAt, "Equipment",
            ProcessLotId: $"LOT_{suffix}", WorkOrderId: $"WO_{suffix}",
            TraceId: $"TRACE_{suffix}", ConditionSnapshotJson: "{\"pressure\":2.1}",
            WorkScopeId: $"SCOPE_{suffix}", CarrierId: $"CARRIER_{suffix}");

        var created = await service.RecordExecutionAsync(command, "operator-1");
        var replay = await service.RecordExecutionAsync(command, "operator-1");
        var stored = await executions.GetExecutionAsync(command.ExecutionId);

        assigned.IsSuccess.Should().BeTrue();
        created.IsSuccess.Should().BeTrue();
        replay.IsSuccess.Should().BeTrue();
        replay.Value.IsReplay.Should().BeTrue();
        stored.Should().NotBeNull();
        stored!.AppliedBy.Should().Be("operator-1");
        stored.IdempotencyKey.Should().Be(command.IdempotencyKey);
        stored.RecipeSnapshotJson.Should().Contain($"\"recipeId\":\"{recipe.Id}\"");
        stored.RecipeSnapshotJson.Should().Contain($"\"version\":{recipe.Version}");
        stored.RecipeSnapshotJson.Should().Contain($"\"assignmentId\":\"{assigned.Value.AssignmentId}\"");
        stored.ParameterSnapshotJson.Should().Contain("Temperature");
        stored.ConditionSnapshotJson.Should().Be("{\"pressure\":2.1}");
        stored.WorkScopeId.Should().Be(command.WorkScopeId);
        stored.CarrierId.Should().Be(command.CarrierId);
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
        await AddRecipeAsync(recipes, recipe);
        var snapshot = new RecipeExecutionSnapshot(
            $"EXE_DRAFT_{suffix}", $"IDEM_DRAFT_{suffix}", new string('A', 64),
            "PLANT01", $"EQ_{suffix}", null, null, null, recipe.Id, recipe.Version,
            "{}", "[]", null, "operator-1", DateTime.UtcNow, "Equipment", null, DateTime.UtcNow);

        (await executions.TryAddAssignedExecutionAsync(
            snapshot, $"MISSING_ASSIGN_{suffix}", $"CLASS_{suffix}")).Should().BeFalse();
        (await executions.GetExecutionAsync(snapshot.ExecutionId)).Should().BeNull();
    }

    [Fact]
    public async Task Repository_guard_rejects_recipe_for_the_wrong_equipment_class_and_legacy_bypass()
    {
        var (recipes, _) = Repositories();
        var executions = ExecutionRepository();
        var suffix = Suffix();
        var equipmentId = $"EQ_CLASS_GUARD_{suffix}";
        var recipe = ReleasedRecipe($"R_CLASS_GUARD_{suffix}", $"WASHER_{suffix}");
        await AddRecipeAsync(recipes, recipe);
        SeedEquipment(equipmentId, recipe.EquipmentClassId);
        var assignment = new RecipeEquipmentAssignment(
            $"A_CLASS_GUARD_{suffix}", equipmentId, null, recipe.Id, recipe.Version,
            DateTime.UtcNow.AddMinutes(-1), null, "engineer-1", true);
        (await executions.TrySaveReleasedAssignmentAsync(assignment)).Should().BeTrue();
        var snapshot = new RecipeExecutionSnapshot(
            $"EXE_CLASS_GUARD_{suffix}", $"IDEM_CLASS_GUARD_{suffix}", new string('C', 64),
            "PLANT01", equipmentId, null, null, null, recipe.Id, recipe.Version,
            "{}", "[]", null, "operator-1", DateTime.UtcNow,
            "Equipment", null, DateTime.UtcNow);

        (await executions.TryAddAssignedExecutionAsync(
            snapshot, assignment.AssignmentId, $"ETCHER_{suffix}")).Should().BeFalse();
        (await executions.TryAddExecutionAsync(snapshot)).Should().BeFalse();
        (await executions.GetExecutionAsync(snapshot.ExecutionId)).Should().BeNull();
    }
}
