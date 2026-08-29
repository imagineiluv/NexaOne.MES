using NexaOne.Common;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — MDM 설비 생성/상태전이(Valid→Invalid 비활성)/갱신. 도메인 불변식이
/// 있는 쓰기만 노출한다(순수 조회는 게이트웨이 MDM.xml이 커버). plugin(MDM)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. Result로 분기(Conflict/Validation/NotFound/Success)를 손실 없이 전달한다.</summary>
public interface IMdmEquipmentBridge : INexaModuleBridge
{
    Task<Result<EquipmentDto>> CreateEquipmentAsync(
        string equipmentId, string equipmentName, string plantId, string areaId, string equipmentType,
        string? parentEquipmentId = null, string vendor = "", string model = "", string equipmentClassId = "",
        CancellationToken ct = default);

    Task<Result> DeactivateEquipmentAsync(string equipmentId, CancellationToken ct = default);

    Task<Result<EquipmentDto>> UpdateEquipmentAsync(
        string equipmentId, string name, string description, string equipmentType, string vendor, string model,
        CancellationToken ct = default);
}
