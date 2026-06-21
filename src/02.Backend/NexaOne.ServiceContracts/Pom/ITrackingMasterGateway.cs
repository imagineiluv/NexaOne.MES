namespace NexaOne.ServiceContracts.Pom;

/// <summary>TrackIn 검증에 필요한 설비 정보 — MDM Equipment의 축소 뷰.</summary>
public sealed record TrackingEquipmentInfo(
    string EquipmentId, string PlantId, string EquipmentClassId, bool IsValid);

/// <summary>
/// 교차 모듈 마스터 조회 포트 (설계 19.4.2의 IEquipmentRepository/IRecipeMappingService/불량 코드 검증 적응).
/// PPM 모듈은 MDM/RMS/QMS를 직접 참조하지 않으므로 API 조립 계층에서 어댑터로 구현한다.
/// </summary>
public interface ITrackingMasterGateway
{
    Task<TrackingEquipmentInfo?> GetEquipmentAsync(string equipmentId, CancellationToken ct = default);

    /// <summary>
    /// Recipe 검증 — Released 상태이고 설비 클래스가 일치해야 한다.
    /// 버전이 주어지면 현재 Recipe 버전과 일치해야 한다 (설계 19.4.4 자동 매핑 검증의 적응).
    /// </summary>
    Task<bool> IsUsableRecipeAsync(
        string recipeDefId, int? recipeDefVersion, string equipmentClassId, CancellationToken ct = default);

    /// <summary>TrackOut 불량 코드 검증 — QMS 불량 분류(활성)에 존재해야 한다.</summary>
    Task<bool> IsValidDefectCodeAsync(string defectCode, CancellationToken ct = default);
}
