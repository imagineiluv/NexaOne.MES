using Microsoft.Extensions.Configuration;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Rms;
using NexaOne.ServiceContracts.Sys;

namespace NexaOne.Project.Cleaner;

/// <summary>
/// Cleaner-only authority composition. All hashes are read from immutable directories; none are
/// accepted from a command. Configuration identifies the expected deployed product, but never
/// enables authority when any value or evidence is missing.
/// </summary>
public sealed class CleanerWorkScopeProjectionAuthorityValidator
    : IWorkScopeProjectionAuthorityValidatorV2
{
    private readonly IWorkScopeAuthorityEvidenceDirectory _workScopes;
    private readonly ICanonicalRecipeExecutionEvidenceDirectory _recipes;
    private readonly IReleasedProgramArtifactDirectory _programs;
    private readonly CleanerProjectionAuthorityProfile _profile;

    public CleanerWorkScopeProjectionAuthorityValidator(
        IWorkScopeAuthorityEvidenceDirectory workScopes,
        ICanonicalRecipeExecutionEvidenceDirectory recipes,
        IReleasedProgramArtifactDirectory programs,
        CleanerProjectionAuthorityProfile profile)
    {
        _workScopes = workScopes ?? throw new ArgumentNullException(nameof(workScopes));
        _recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        _programs = programs ?? throw new ArgumentNullException(nameof(programs));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
    }

    public async Task<WorkScopeProjectionAuthorityValidationDecision> ValidateAsync(
        WorkScopeProjectionAuthorityProvisionCommand command,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_profile.Enabled || !_profile.IsComplete)
            return Reject("Projection.Authority.CleanerProfileDisabled", "Cleaner authority profile is disabled or incomplete.");

        var scope = await _workScopes.FindAsync(command.WorkScopeId, ct).ConfigureAwait(false);
        if (scope is null) return Reject("Projection.Authority.WorkScopeMissing", "WorkScope evidence was not found.");
        if (!Eq(scope.WorkScopeId, command.WorkScopeId)
            || !Eq(scope.ScopeType, nameof(WorkScopeType.Other))
            || !Eq(scope.TargetId, command.PairRunId)
            || !Eq(scope.EquipmentId, command.EquipmentId)
            || !Eq(scope.ProcessId, command.OperationKey))
            return Reject("Projection.Authority.WorkScopeMismatch", "WorkScope equipment or operation does not match.");

        var recipe = await _recipes.FindAsync(command.RecipeExecutionId, ct).ConfigureAwait(false);
        if (recipe is null) return Reject("Projection.Authority.RecipeEvidenceMissing", "Canonical recipe execution evidence was not found.");
        if (!Schema(recipe.SnapshotSchema) || !Hash(recipe.SnapshotHash))
            return Reject("Projection.Authority.LegacyRecipeEvidence", "Recipe evidence has no canonical schema and hash.");
        if (!Eq(recipe.ExecutionId, command.RecipeExecutionId)
            || !Eq(recipe.WorkScopeId, command.WorkScopeId)
            || !Eq(recipe.PairRunId, command.PairRunId)
            || !Eq(recipe.SequenceRunId, command.SequenceRunId)
            || !Eq(recipe.EquipmentId, command.EquipmentId)
            || !Eq(recipe.OperationKey, command.OperationKey)
            || !Eq(scope.RecipeId, recipe.RecipeId)
            || scope.RecipeVersion != recipe.RecipeVersion)
            return Reject("Projection.Authority.RecipeMismatch", "Recipe execution evidence does not match the WorkScope command.");

        var program = await _programs.FindAsync(command.ProgramArtifactId, ct).ConfigureAwait(false);
        if (program is null) return Reject("Projection.Authority.ProgramUnreleased", "A released program artifact was not found.");
        if (program.IsRevoked) return Reject("Projection.Authority.ProgramRevoked", "The released program artifact is revoked for new authority.");
        if (!Eq(program.ArtifactId, command.ProgramArtifactId)
            || !Eq(program.EquipmentId, command.EquipmentId)
            || !Eq(program.OperationKey, command.OperationKey)
            || !Eq(program.ProductProfileId, _profile.ProductProfileId)
            || !Eq(program.PluginId, _profile.PluginId)
            || !Eq(program.ProductDefinitionVersion, _profile.ProductDefinitionVersion)
            || !Eq(program.ProgramVersion, _profile.ProgramVersion)
            || !Eq(program.ProgramSchema, _profile.ProgramSchema)
            || !Hash(program.ProgramHash)
            || !Eq(program.BoundRecipeSnapshotSchema, recipe.SnapshotSchema)
            || !Eq(program.BoundRecipeSnapshotHash, recipe.SnapshotHash))
            return Reject("Projection.Authority.ProgramMismatch", "Released program evidence does not exactly match the Cleaner profile and recipe binding.");

        return WorkScopeProjectionAuthorityValidationDecision.Accepted(
            new WorkScopeProjectionAuthorityEvidence(
            command.WorkScopeId, command.SourceClientId, command.EquipmentId, command.OperationKey,
            command.PairRunId, command.SequenceRunId, recipe.ExecutionId, recipe.RecipeId,
            recipe.RecipeVersion, recipe.SnapshotSchema!, recipe.SnapshotHash!.ToUpperInvariant(),
            program.ArtifactId, program.ProgramSchema, program.ProgramHash.ToUpperInvariant()));
    }

    private static WorkScopeProjectionAuthorityValidationDecision Reject(string code, string message) =>
        WorkScopeProjectionAuthorityValidationDecision.Rejected(code, message);
    private static bool Eq(string? left, string? right) => string.Equals(left, right, StringComparison.Ordinal);
    private static bool Schema(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 100;
    private static bool Hash(string? value) => value is { Length: 64 }
        && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
}

public sealed record CleanerProjectionAuthorityProfile(
    bool Enabled,
    string ProductProfileId,
    string PluginId,
    string ProductDefinitionVersion,
    string ProgramVersion,
    string ProgramSchema)
{
    private const string ConfigurationPrefix =
        "Projects:Cleaner:WorkScopeProjectionAuthority:";

    /// <summary>
    /// Captures one immutable profile when the Cleaner Spring context is created. Configuration
    /// values are not trimmed or normalized; an invalid raw coordinate remains fail-closed.
    /// </summary>
    public CleanerProjectionAuthorityProfile(IConfiguration configuration)
        : this(
            ReadEnabled(configuration),
            Read(configuration, "ProductProfileId"),
            Read(configuration, "PluginId"),
            Read(configuration, "ProductDefinitionVersion"),
            Read(configuration, "ProgramVersion"),
            Read(configuration, "ProgramSchema"))
    {
    }

    public bool IsComplete => Enabled
        && Present(ProductProfileId, 100)
        && Present(PluginId, 200)
        && Present(ProductDefinitionVersion, 100)
        && Present(ProgramVersion, 100)
        && Present(ProgramSchema, 100);

    private static bool ReadEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return string.Equals(
            configuration[ConfigurationPrefix + "Enabled"],
            "true",
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(IConfiguration configuration, string name) =>
        configuration[ConfigurationPrefix + name] ?? string.Empty;

    private static bool Present(string? value, int maxLength) =>
        value is { Length: > 0 }
        && value.Length <= maxLength
        && string.Equals(value, value.Trim(), StringComparison.Ordinal)
        && value.All(static character => !char.IsControl(character));
}
