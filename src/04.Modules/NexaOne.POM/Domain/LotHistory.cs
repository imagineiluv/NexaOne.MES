namespace NexaOne.POM.Domain;

/// <summary>설계 19.4.6 STD_TB_LOT_HISTORY의 ExecutionId 값.</summary>
public static class LotExecutionId
{
    public const string TrackIn = "TrackIn";
    public const string TrackOut = "TrackOut";
    public const string Finish = "Finish";
    public const string Consume = "Consume";
}

/// <summary>
/// Lot 추적 이력 (설계 19.4.6, 현행 WpmTbLotTraceData). 행은 추가만 되고 수정되지 않는다.
/// LotHistoryId는 DB IDENTITY — 조회 행에만 채워진다.
/// </summary>
public sealed record LotHistory(
    long LotHistoryId,
    string PlantId,
    string LotId,
    string? EquipmentId,
    string ProcessId,
    string? RecipeDefId,
    int? RecipeDefVersion,
    DateTime? TrackInTime,
    DateTime? TrackOutTime,
    string ExecutionId,
    string ExecutionUser,
    decimal Qty,
    decimal DefectQty,
    string LotState,
    string ProcessState,
    DateTime CreatedAt)
{
    /// <summary>신규 기록용 — IDENTITY/CreatedAt은 DB가 채운다.</summary>
    public static LotHistory Of(
        Lot lot, string executionId, string executionUser, decimal qty, decimal defectQty) =>
        new(0, lot.PlantId, lot.Id, lot.EquipmentId, lot.CurrentProcessId,
            lot.RecipeDefId, lot.RecipeDefVersion, lot.TrackInTime, lot.TrackOutTime,
            executionId, executionUser, qty, defectQty,
            lot.State.ToString(), lot.ProcessState.ToString(), DateTime.UtcNow);
}

/// <summary>Mixing 입력/출력 관계 (설계 19.4.7 STD_TB_LOT_MIXING_RELATION 적응).</summary>
public sealed record LotMixingRelation(
    string PlantId,
    string OutputLotId,
    string InputLotId,
    decimal InputQty,
    decimal? MixingRate,
    DateTime ConsumedAt,
    string ConsumedBy);
