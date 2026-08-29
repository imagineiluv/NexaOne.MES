using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>
/// MDM과 EST의 형제 Spring 컨텍스트를 연결하는 부모 컨텍스트 proxy입니다.
/// 설비·캐리어 master SQL과 해석은 MDM 모듈의 adapter가 소유합니다.
/// </summary>
public sealed class EquipmentOutputMasterDirectoryProxy : IEquipmentOutputMasterDirectory
{
    private readonly ModuleBeanResolver _resolver;

    public EquipmentOutputMasterDirectoryProxy(ModuleBeanResolver resolver)
        => _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));

    public Task<EquipmentOutputMasterScopeDto?> GetScopeAsync(
        string equipmentId,
        string? carrierId,
        CancellationToken ct = default)
        => Resolve().GetScopeAsync(equipmentId, carrierId, ct);

    private IEquipmentOutputMasterDirectory Resolve() =>
        _resolver.Resolve<IEquipmentOutputMasterDirectory>(
            "Mdm", "equipmentOutputMasterDirectory");
}
