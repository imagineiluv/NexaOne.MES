using Microsoft.Extensions.Configuration;
using NexaOne.Infrastructure.Persistence;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Infrastructure;
using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.RMS;

/// <summary>RMS 내부 구현 그래프를 숨기고 Recipe 공개 인터페이스만 노출하는 조립 진입점입니다.</summary>
public sealed class Module
{
    private readonly IRecipeApprovalBridge _recipeBridge;
    private readonly IRecipeExecutionBridge _executionBridge;
    private readonly ITrackingRecipeDirectory _trackingRecipeDirectory;
    private readonly ICanonicalRecipeExecutionEvidenceDirectory _canonicalExecutionEvidenceDirectory;
    private readonly ISqliteSchemaContribution _trustedAuthoritySqliteSchemaContribution;

    public Module(
        EesDataSource dataSource,
        IConfiguration configuration,
        IEquipmentDirectory equipmentDirectory)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(equipmentDirectory);

        var recipes = new RecipeRepository(dataSource, configuration);
        var parameters = new RecipeParamRepository(dataSource);
        var executions = new RecipeExecutionRepository(dataSource);
        _recipeBridge = new RecipeBridge(new RecipeService(recipes, parameters));
        _executionBridge = new RecipeExecutionBridge(
            new RecipeExecutionService(recipes, parameters, executions, equipmentDirectory));
        _trackingRecipeDirectory = new TrackingRecipeDirectory(dataSource);
        _canonicalExecutionEvidenceDirectory = new CanonicalRecipeExecutionEvidenceDirectory(dataSource);
        _trustedAuthoritySqliteSchemaContribution =
            new RmsTrustedAuthoritySqliteSchemaContribution();
    }

    public IRecipeApprovalBridge GetRecipeBridge() => _recipeBridge;
    public IRecipeExecutionBridge GetExecutionBridge() => _executionBridge;
    public ITrackingRecipeDirectory GetTrackingRecipeDirectory() => _trackingRecipeDirectory;
    public ICanonicalRecipeExecutionEvidenceDirectory GetCanonicalExecutionEvidenceDirectory() => _canonicalExecutionEvidenceDirectory;
    public ISqliteSchemaContribution GetTrustedAuthoritySqliteSchemaContribution() =>
        _trustedAuthoritySqliteSchemaContribution;
}
