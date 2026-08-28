using NexaOne.ServiceContracts.Rms;

namespace NexaOne.Server.Gateway;

/// <summary>RMS Recipe 사용 가능성 directory를 POM 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class TrackingRecipeDirectoryProxy : ITrackingRecipeDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public TrackingRecipeDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<bool> IsUsableAsync(
        string recipeDefId,
        int? recipeDefVersion,
        string equipmentClassId,
        CancellationToken ct = default)
        => _resolver.Resolve<ITrackingRecipeDirectory>("Rms", "trackingRecipeDirectory")
            .IsUsableAsync(recipeDefId, recipeDefVersion, equipmentClassId, ct);
}
