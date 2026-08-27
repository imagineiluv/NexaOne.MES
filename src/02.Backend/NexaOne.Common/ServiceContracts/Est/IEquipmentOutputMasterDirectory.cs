namespace NexaOne.ServiceContracts.Est;

/// <summary>
/// EST 출력 원장이 소유하지 않는 설비·캐리어 마스터를 확인하는 호스트 포트입니다.
/// 호스트 어댑터가 MDM 물리 스키마를 숨기고, EST 플러그인은 출력 기록 전에
/// 설비의 plant/활성 상태와 선택 캐리어의 존재 여부만 확인합니다.
/// </summary>
public interface IEquipmentOutputMasterDirectory
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
