using NexaOne.ServiceContracts.Est;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>MDM OEE 계획 directory를 EST 형제 컨텍스트에 연결하는 부모 프록시입니다.</summary>
public sealed class OeePlanDirectoryProxy : IOeePlanDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public OeePlanDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<OeePlanSnapshotDto> LoadPlanAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime? localDay,
        CancellationToken ct = default)
        => Resolve().LoadPlanAsync(targetEquipmentIds, localDay, ct);

    public Task<IReadOnlyList<OeePlantLocalDateDto>> LoadPlantLocalDatesAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime utcNow,
        CancellationToken ct = default)
        => Resolve().LoadPlantLocalDatesAsync(targetEquipmentIds, utcNow, ct);

    public Task<IReadOnlyDictionary<string, string>> LoadProductUnitsAsync(
        IReadOnlyList<string> productIds,
        CancellationToken ct = default)
        => Resolve().LoadProductUnitsAsync(productIds, ct);

    private IOeePlanDirectory Resolve() =>
        _resolver.Resolve<IOeePlanDirectory>("Mdm", "oeePlanDirectory");
}
