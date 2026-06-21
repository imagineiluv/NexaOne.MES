namespace NexaOne.ServiceContracts.Qms;

// 도메인 엔티티(Defect/SpcParam)를 직렬화 계약으로 노출하지 않는 경량 DTO(ALC/버전 결합 차단).
// QmsService 반환/도메인 컬럼과 1:1. bool/enum은 원시값으로 평탄화한다.
public record DefectDto(
    string DefectId, string LotId, string EquipmentId, string DefectClassId,
    int DefectCount, decimal DefectRate, DateTime InspectedAt, string InspectorId,
    bool IsConfirmed, DateTime? ConfirmedAt, string? Remark);

public record SpcParamDto(
    string ParamId, string ParamName, string EquipmentId, string ProcessId,
    decimal Mean, decimal Ucl, decimal Lcl, decimal? Usl, decimal? Lsl,
    int SampleSize, bool IsActive);
