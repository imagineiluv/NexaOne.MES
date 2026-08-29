using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// 설비 출력 기록에 필요한 설비·캐리어 마스터 snapshot을 MDM이 제공하는 typed seam입니다.
/// 소비 모듈은 MDM 물리 스키마를 알지 않고 plant/유효 상태와 선택 캐리어 존재 여부만 확인합니다.
/// </summary>
public interface IEquipmentOutputMasterDirectory : INexaModuleBridge
{
    Task<EquipmentOutputMasterScopeDto?> GetScopeAsync(
        string equipmentId,
        string? carrierId,
        CancellationToken ct = default);
}

public sealed record EquipmentOutputMasterScopeDto(
    string EquipmentId,
    string PlantId,
    bool IsEquipmentValid,
    bool CarrierExists);
