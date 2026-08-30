using Microsoft.Extensions.Configuration;
using NexaOne.Project.Cleaner;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Rms;
using NexaOne.ServiceContracts.Sys;
using Xunit;

namespace NexaOne.ServerTests;

public sealed class CleanerProjectionAuthorityValidatorTests
{
    private const string ProfilePrefix = "Projects:Cleaner:WorkScopeProjectionAuthority:";
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public void Cleaner_implements_only_the_contract_owned_V2_validator_seam()
    {
        Assert.True(typeof(IWorkScopeProjectionAuthorityValidatorV2)
            .IsAssignableFrom(typeof(CleanerWorkScopeProjectionAuthorityValidator)));
        Assert.False(typeof(IWorkScopeProjectionAuthorityValidator)
            .IsAssignableFrom(typeof(CleanerWorkScopeProjectionAuthorityValidator)));
    }

    [Fact]
    public async Task Valid_immutable_directories_supply_authority_hashes()
    {
        var validator = Create();
        var result = await validator.ValidateAsync(Command());

        Assert.True(result.IsAccepted);
        Assert.NotNull(result.Evidence);
        Assert.Equal(HashA, result.Evidence!.RecipeSnapshotHash);
        Assert.Equal(HashB, result.Evidence.ProgramHash);
    }

    [Fact]
    public async Task Legacy_recipe_without_schema_hash_fails_closed()
    {
        var validator = Create(recipe: Recipe() with { SnapshotSchema = null, SnapshotHash = null });
        var result = await validator.ValidateAsync(Command());
        Assert.False(result.IsAccepted);
        Assert.Equal("Projection.Authority.LegacyRecipeEvidence", result.RejectionCode);
    }

    [Fact]
    public async Task Revoked_program_blocks_new_authority()
    {
        var revoked = Program() with
        {
            Revocation = new("REV-1", "ART-1", DateTime.UtcNow, "operator", "rollback")
        };
        var result = await Create(program: revoked).ValidateAsync(Command());
        Assert.False(result.IsAccepted);
        Assert.Equal("Projection.Authority.ProgramRevoked", result.RejectionCode);
    }

    [Fact]
    public async Task Identity_comparison_is_ordinal_and_case_sensitive()
    {
        var result = await Create().ValidateAsync(Command() with { EquipmentId = "eq-1" });
        Assert.False(result.IsAccepted);
    }

    [Fact]
    public async Task Disabled_profile_fails_before_reading_directories()
    {
        var profile = Profile() with { Enabled = false };
        var validator = new CleanerWorkScopeProjectionAuthorityValidator(
            new ThrowingWorkScopes(),
            new ThrowingRecipes(),
            new ThrowingPrograms(),
            profile);

        var result = await validator.ValidateAsync(Command());
        Assert.False(result.IsAccepted);
        Assert.Equal("Projection.Authority.CleanerProfileDisabled", result.RejectionCode);
    }

    [Fact]
    public void Configuration_profile_defaults_off_and_captures_raw_values_once()
    {
        var empty = new CleanerProjectionAuthorityProfile(new ConfigurationBuilder().Build());
        Assert.False(empty.Enabled);
        Assert.False(empty.IsComplete);
        Assert.Equal(string.Empty, empty.ProductProfileId);

        var configuration = ValidProfileConfiguration();
        var captured = new CleanerProjectionAuthorityProfile(configuration);
        configuration[ProfilePrefix + "ProductProfileId"] = "changed-after-context-creation";

        Assert.True(captured.Enabled);
        Assert.True(captured.IsComplete);
        Assert.Equal("cleaner", captured.ProductProfileId);
    }

    [Fact]
    public void Configuration_profile_does_not_trim_or_accept_boundary_whitespace()
    {
        var configuration = ValidProfileConfiguration();
        configuration[ProfilePrefix + "ProductProfileId"] = " cleaner ";

        var captured = new CleanerProjectionAuthorityProfile(configuration);

        Assert.Equal(" cleaner ", captured.ProductProfileId);
        Assert.False(captured.IsComplete);

        configuration = ValidProfileConfiguration();
        configuration[ProfilePrefix + "Enabled"] = " true ";
        Assert.False(new CleanerProjectionAuthorityProfile(configuration).Enabled);
    }

    [Fact]
    public void Configuration_profile_enforces_v159_lengths_and_control_free_coordinates()
    {
        Assert.True((Profile() with
        {
            ProductProfileId = new string('P', 100),
            PluginId = new string('L', 200),
            ProductDefinitionVersion = new string('D', 100),
            ProgramVersion = new string('V', 100),
            ProgramSchema = new string('S', 100),
        }).IsComplete);

        var invalid = new[]
        {
            Profile() with { ProductProfileId = new string('P', 101) },
            Profile() with { PluginId = new string('L', 201) },
            Profile() with { ProductDefinitionVersion = new string('D', 101) },
            Profile() with { ProgramVersion = new string('V', 101) },
            Profile() with { ProgramSchema = new string('S', 101) },
            Profile() with { ProgramSchema = "cleaner\u0001schema" },
            Profile() with { PluginId = "\tplugin.cleaner" },
        };

        Assert.All(invalid, static profile => Assert.False(profile.IsComplete));
    }

    public static IEnumerable<object[]> CommandMismatches()
    {
        yield return [Command() with { WorkScopeId = "ws-1" }];
        yield return [Command() with { PairRunId = "pair-1" }];
        yield return [Command() with { SequenceRunId = "seq-1" }];
        yield return [Command() with { EquipmentId = "eq-1" }];
        yield return [Command() with { OperationKey = "cleaning" }];
        yield return [Command() with { RecipeExecutionId = "exec-1" }];
        yield return [Command() with { ProgramArtifactId = "art-1" }];
    }

    [Theory]
    [MemberData(nameof(CommandMismatches))]
    public async Task Every_command_identity_is_exact(WorkScopeProjectionAuthorityProvisionCommand command) =>
        Assert.False((await Create().ValidateAsync(command)).IsAccepted);

    [Fact]
    public async Task Lowercase_or_malformed_hash_fails_closed()
    {
        Assert.False((await Create(recipe: Recipe() with { SnapshotHash = HashA.ToLowerInvariant() }).ValidateAsync(Command())).IsAccepted);
        Assert.False((await Create(program: Program() with { ProgramHash = "NOT-A-HASH" }).ValidateAsync(Command())).IsAccepted);
    }

    [Fact]
    public async Task Incomplete_profile_fails_closed()
    {
        var result = await Create(profile: Profile() with { PluginId = "" }).ValidateAsync(Command());
        Assert.False(result.IsAccepted);
        Assert.Equal("Projection.Authority.CleanerProfileDisabled", result.RejectionCode);
    }

    [Fact]
    public async Task Missing_directories_fail_closed()
    {
        Assert.False((await Create(missingRecipe: true).ValidateAsync(Command())).IsAccepted);
        Assert.False((await Create(missingProgram: true).ValidateAsync(Command())).IsAccepted);
        Assert.False((await Create(missingScope: true).ValidateAsync(Command())).IsAccepted);
    }

    private static CleanerWorkScopeProjectionAuthorityValidator Create(
        CanonicalRecipeExecutionEvidenceDto? recipe = null,
        ReleasedProgramArtifactDto? program = null,
        CleanerProjectionAuthorityProfile? profile = null,
        bool missingRecipe = false,
        bool missingProgram = false,
        bool missingScope = false) => new(
        new WorkScopes(missingScope ? null : Scope()),
        new Recipes(missingRecipe ? null : recipe ?? Recipe()),
        new Programs(missingProgram ? null : program ?? Program()),
        profile ?? Profile());

    private static CleanerProjectionAuthorityProfile Profile() =>
        new(true, "cleaner", "plugin.cleaner", "product-v1", "program-v1", "cleaner-program/v2");
    private static ConfigurationManager ValidProfileConfiguration()
    {
        var configuration = new ConfigurationManager();
        configuration[ProfilePrefix + "Enabled"] = "true";
        configuration[ProfilePrefix + "ProductProfileId"] = "cleaner";
        configuration[ProfilePrefix + "PluginId"] = "plugin.cleaner";
        configuration[ProfilePrefix + "ProductDefinitionVersion"] = "product-v1";
        configuration[ProfilePrefix + "ProgramVersion"] = "program-v1";
        configuration[ProfilePrefix + "ProgramSchema"] = "cleaner-program/v2";
        return configuration;
    }

    private static WorkScopeProjectionAuthorityProvisionCommand Command() =>
        new("WS-1", "client", "EQ-1", "CLEANING", "PAIR-1", "SEQ-1", "EXEC-1", "ART-1", "idem", "operator");
    private static CanonicalRecipeExecutionEvidenceDto Recipe() =>
        new("EXEC-1", "WS-1", "PAIR-1", "SEQ-1", "EQ-1", "CLEANING", "RCP-1", 2, "cleaner-recipe/v1", HashA, DateTime.UtcNow);
    private static ReleasedProgramArtifactDto Program() =>
        new("ART-1", "EQ-1", "CLEANING", "cleaner", "plugin.cleaner", "product-v1", "program-v1",
            "cleaner-program/v2", HashB, "cleaner-recipe/v1", HashA, DateTime.UtcNow, "release");
    private static WorkScopeDto Scope() => new(
        "WS-1", "P1", "Other", "PAIR-1", "pair", null, "EQ-1", null, "CLEANING", "RCP-1", 2,
        2, 0, 0, 0, null, "Created", false, null, null, null, 1, DateTime.UtcNow, "operator", null, null);

    private sealed class Recipes(CanonicalRecipeExecutionEvidenceDto? value) : ICanonicalRecipeExecutionEvidenceDirectory
    {
        public Task<CanonicalRecipeExecutionEvidenceDto?> FindAsync(string executionId, CancellationToken ct = default) => Task.FromResult(value);
    }
    private sealed class Programs(ReleasedProgramArtifactDto? value) : IReleasedProgramArtifactDirectory
    {
        public Task<ReleasedProgramArtifactDto?> FindAsync(string artifactId, CancellationToken ct = default) => Task.FromResult(value);
    }
    private sealed class WorkScopes(WorkScopeDto? value) : IWorkScopeAuthorityEvidenceDirectory
    {
        public Task<WorkScopeDto?> FindAsync(string workScopeId, CancellationToken ct = default) =>
            Task.FromResult(value is not null && string.Equals(workScopeId, value.WorkScopeId, StringComparison.Ordinal) ? value : null);
    }

    private sealed class ThrowingRecipes : ICanonicalRecipeExecutionEvidenceDirectory
    {
        public Task<CanonicalRecipeExecutionEvidenceDto?> FindAsync(
            string executionId,
            CancellationToken ct = default) => throw new InvalidOperationException("must not resolve RMS");
    }

    private sealed class ThrowingPrograms : IReleasedProgramArtifactDirectory
    {
        public Task<ReleasedProgramArtifactDto?> FindAsync(
            string artifactId,
            CancellationToken ct = default) => throw new InvalidOperationException("must not resolve SYS");
    }

    private sealed class ThrowingWorkScopes : IWorkScopeAuthorityEvidenceDirectory
    {
        public Task<WorkScopeDto?> FindAsync(
            string workScopeId,
            CancellationToken ct = default) => throw new InvalidOperationException("must not resolve POM");
    }
}
