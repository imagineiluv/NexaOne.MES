using NexaFramework;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.Server.Gateway;

/// <summary>
/// MDM과 소비 모듈의 형제 Spring 컨텍스트를 연결하는 부모 컨텍스트 프록시입니다.
/// 설비 규칙과 SQL은 MDM 모듈에 남고 이 형식은 공용 계약으로만 위임합니다.
/// </summary>
public sealed class EquipmentDirectoryProxy : IEquipmentDirectory
{
    public Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(
        string plantId,
        CancellationToken ct = default)
        => Resolve().GetEquipmentIdsByPlantAsync(plantId, ct);

    public Task<EquipmentDirectoryEntry?> GetEquipmentAsync(
        string equipmentId,
        CancellationToken ct = default)
        => Resolve().GetEquipmentAsync(equipmentId, ct);

    public Task<bool> EquipmentClassExistsAsync(
        string equipmentClassId,
        CancellationToken ct = default)
        => Resolve().EquipmentClassExistsAsync(equipmentClassId, ct);

    private static IEquipmentDirectory Resolve()
    {
        var bean = ApplicationServer.GetInstance().GetBean("Mdm", "equipmentDirectory");
        return bean as IEquipmentDirectory
            ?? throw ModuleProxy.TypeMismatch<IEquipmentDirectory>("Mdm", "equipmentDirectory", bean);
    }
}
