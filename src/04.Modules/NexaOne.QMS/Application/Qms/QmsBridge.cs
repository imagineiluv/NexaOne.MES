using NexaOne.Common;
using NexaOne.QMS.Domain;
using NexaOne.ServiceContracts.Qms;

namespace NexaOne.QMS.Application.Qms;

/// <summary>ADR-008 얇은 브리지 어댑터 — QmsService에 위임하고 Defect/SpcParam을 계약 DTO로 매핑(bool/enum 평탄화).
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IQmsBridge로 캐스트해 DI에 등록한다. 상태/불변식 연산만 노출한다.</summary>
public sealed class QmsBridge : IQmsBridge
{
    private readonly QmsService _service;
    public QmsBridge(QmsService service) => _service = service;

    public async Task<IReadOnlyList<DefectDto>> GetDefectsByLotAsync(string lotId, CancellationToken ct = default)
    {
        var r = await _service.GetDefectsByLotAsync(lotId, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : new List<DefectDto>();
    }

    public Task<Result> ConfirmDefectAsync(string defectId, string confirmerId, CancellationToken ct = default)
        => _service.ConfirmDefectAsync(defectId, confirmerId, ct);

    public Task<Result> UpdateControlLimitsAsync(string paramId, decimal mean, decimal ucl, decimal lcl, CancellationToken ct = default)
        => _service.UpdateSpcControlLimitsAsync(paramId, mean, ucl, lcl, ct);

    private static DefectDto ToDto(Defect d)
        => new(d.Id, d.LotId, d.EquipmentId, d.DefectClassId, d.DefectCount, d.DefectRate,
            d.InspectedAt, d.InspectorId, d.IsConfirmed, d.ConfirmedAt, d.Remark);
}
