namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// 교차 모듈 설비 디렉터리 조회 포트 — 호스트 조립 루트(Default ALC)가 구현해 노출한다.
/// 플러그인(예: EST)이 plantId로 소속 설비 목록을 얻을 때 MDM 물리 스키마(MDM_EQUIPMENT)를
/// 자신의 SQL에 박지 않고 이 포트로 위임하도록 한다(ADR-006: 모듈은 타 모듈 스키마 미보유).
/// 포트는 ServiceContracts에 공유되어 plugin/Default ALC 간 타입 동일성이 유지되며, 구현(MDM read)은 호스트가 소유한다.
/// </summary>
public interface IEquipmentDirectory
{
    /// <summary>지정 plantId에 소속된 설비 ID 목록을 반환한다(없으면 빈 목록).</summary>
    Task<IReadOnlyList<string>> GetEquipmentIdsByPlantAsync(string plantId, CancellationToken ct = default);
}
