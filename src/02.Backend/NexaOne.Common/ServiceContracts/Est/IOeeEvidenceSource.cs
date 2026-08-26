namespace NexaOne.ServiceContracts.Est;

/// <summary>
/// OEE 모듈이 소유하지 않는 계획·생산 증거를 읽는 호스트 seam입니다.
/// 구현 adapter가 MDM/POM 물리 스키마와 시간대·교대 해석을 숨기므로 EST 모듈은
/// 다른 모듈의 테이블 이름이나 배포 형태를 알 필요가 없습니다.
/// </summary>
public interface IOeeEvidenceSource
{
    /// <summary>
    /// OEE 목표 설비의 plant 소속을 해석합니다. <paramref name="localDay"/>가 있으면
    /// plant 시간대가 적용된 해당 로컬 일자의 UTC 교대 윈도도 함께 반환합니다.
    /// </summary>
    Task<OeePlanSnapshotDto> LoadPlanAsync(
        IReadOnlyList<string> targetEquipmentIds,
        DateTime? localDay,
        CancellationToken ct = default);

    /// <summary>
    /// 지정 UTC 반개구간 [fromUtc, toUtc)의 상위 생산 실행 증거를 한 번에 반환합니다.
    /// LOT 수량은 OEE projection 전환기의 권위 원장 판단에, TrackOut 상세는 takt/cycle 계산에 사용됩니다.
    /// </summary>
    Task<OeeProductionWindowDto> LoadProductionAsync(
        string plantId,
        string equipmentId,
        DateTime fromUtc,
        DateTime toUtc,
        CancellationToken ct = default);
}

/// <summary>목표 설비 소속과 선택 일자의 plant별 교대 계획 snapshot입니다.</summary>
public sealed record OeePlanSnapshotDto(
    IReadOnlyList<OeeEquipmentScopeDto> EquipmentScopes,
    IReadOnlyList<OeePlantDayDto> PlantDays);

/// <summary>OEE 목표 설비가 속한 plant입니다.</summary>
public sealed record OeeEquipmentScopeDto(string EquipmentId, string PlantId);

/// <summary>
/// 한 plant의 로컬 일자 계획입니다. 휴일이거나 적용 가능한 교대가 없으면
/// <see cref="Windows"/>는 빈 목록입니다.
/// </summary>
public sealed record OeePlantDayDto(
    string PlantId,
    bool IsHoliday,
    IReadOnlyList<OeeShiftWindowDto> Windows);

/// <summary>plant 시간대를 적용해 확정된 UTC 교대 윈도입니다.</summary>
public sealed record OeeShiftWindowDto(
    string ShiftId,
    DateTime StartUtc,
    DateTime EndUtc,
    decimal PlannedMinutes);

/// <summary>
/// 한 설비·윈도의 상위 생산 수량과 TrackOut 상세 snapshot입니다.
/// <see cref="LotEventCount"/>가 0보다 크면 LOT 범위에서는 이 snapshot이 권위 있고,
/// 0이면 EST 표준 출력 원장이 단독 권위가 됩니다.
/// </summary>
public sealed record OeeProductionWindowDto(
    int LotEventCount,
    decimal LotTotalCount,
    decimal LotDefectCount,
    IReadOnlyList<OeeTrackOutDto> TrackOuts);

/// <summary>takt/cycle 계산에 필요한 최소 TrackOut 증거입니다.</summary>
public sealed record OeeTrackOutDto(
    string ProductId,
    string ProcessId,
    decimal Qty,
    DateTime? TrackInTimeUtc,
    DateTime TrackOutTimeUtc,
    string QuantityUom);
