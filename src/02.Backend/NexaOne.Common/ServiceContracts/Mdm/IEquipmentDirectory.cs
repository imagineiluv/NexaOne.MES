using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// 교차 모듈 설비 디렉터리 조회 포트 — MDM 모듈이 구현해 노출한다.
/// 모듈(예: EST/EMS/RMS)이 설비·분류를 검증할 때 MDM 물리 스키마(MDM_EQUIPMENT)를
/// 자신의 SQL에 박지 않고 이 포트로 위임하도록 한다(ADR-006: 모듈은 타 모듈 스키마 미보유).
/// 포트는 ServiceContracts에 공유되어 plugin/Default ALC 간 타입 동일성이 유지된다.
/// </summary>
public interface IEquipmentDirectory : INexaModuleBridge
{
    /// <summary>지정 plantId에 소속된 설비 ID 목록을 반환한다(없으면 빈 목록).</summary>
    Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(string plantId, CancellationToken ct = default);

    /// <summary>
    /// 설비에 귀속되는 Recipe/Tool 같은 다른 Module의 실행 규칙이 물리 MDM 테이블을 참조하지 않고
    /// plant, class와 현재 유효성을 검증할 수 있는 축소 snapshot을 반환한다.
    /// </summary>
    Task<EquipmentDirectoryEntry?> GetEquipmentAsync(
        string equipmentId,
        CancellationToken ct = default);

    /// <summary>지정 설비 분류가 MDM에 등록되어 있는지 반환한다.</summary>
    Task<bool> EquipmentClassExistsAsync(
        string equipmentClassId,
        CancellationToken ct = default);
}

public sealed record EquipmentDirectoryEntry(
    string EquipmentId,
    string PlantId,
    string EquipmentClassId,
    bool IsValid);
