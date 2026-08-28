using NexaOne.ServiceContracts;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.ServiceContracts.Mdm;

/// <summary>
/// OEE가 사용하는 설비 소속·공장 달력·교대·시간대의 축소 조회 계약입니다.
/// 시간대 변환까지 MDM adapter가 소유하여 호스트와 EST에 MDM 물리 모델을 노출하지 않습니다.
/// </summary>
public interface IOeePlanDirectory : INexaModuleBridge
{
    Task<OeePlanSnapshotDto> LoadPlanAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime? localDay,
        CancellationToken ct = default);

    Task<IReadOnlyList<OeePlantLocalDateDto>> LoadPlantLocalDatesAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime utcNow,
        CancellationToken ct = default);

    /// <summary>제품별 기준 수량 단위를 반환합니다. 존재하지 않는 제품은 결과에서 제외됩니다.</summary>
    Task<IReadOnlyDictionary<string, string>> LoadProductUnitsAsync(
        IReadOnlyList<string> productIds,
        CancellationToken ct = default);
}
