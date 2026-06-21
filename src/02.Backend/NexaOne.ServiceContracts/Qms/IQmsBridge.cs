using NexaOne.Common;

namespace NexaOne.ServiceContracts.Qms;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — QMS 상태/불변식 연산. 단순 CRUD/조회는 게이트웨이(QMS.xml, 트랙②)가
/// 커버하므로, 이 브리지는 도메인 상태전이만 노출한다: 부적합 확정(재확정 방지 불변식)·SPC 관리한계 갱신(UCL>LCL 불변식).
/// plugin(QMS)이 구현하고 호스트가 GetBean→캐스트로 Default-ALC DI에 등록한다. Result로 분기(Conflict/Validation/Success)를 손실 없이 전달한다.</summary>
public interface IQmsBridge
{
    /// <summary>로트별 부적합 조회 — 확정 워크플로의 확인용 read(조회 자체는 게이트웨이도 가능하나 브리지 사용 편의를 위해 제공).</summary>
    Task<IReadOnlyList<DefectDto>> GetDefectsByLotAsync(string lotId, CancellationToken ct = default);

    /// <summary>부적합 확정. 미확정→확정 상태전이(재확정 시 Conflict). QmsService.ConfirmDefectAsync에 위임.</summary>
    Task<Result> ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default);

    /// <summary>SPC 관리한계(평균/UCL/LCL) 갱신. UCL>LCL 불변식 위반 시 Validation. QmsService.UpdateSpcControlLimitsAsync에 위임.</summary>
    Task<Result> UpdateControlLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default);
}
