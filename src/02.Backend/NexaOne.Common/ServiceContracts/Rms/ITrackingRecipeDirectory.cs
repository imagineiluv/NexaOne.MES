using NexaOne.ServiceContracts;

namespace NexaOne.ServiceContracts.Rms;

/// <summary>
/// LOT 실행 시 사용할 수 있는 Recipe인지 판정하는 RMS 소유 조회 계약입니다.
/// </summary>
public interface ITrackingRecipeDirectory : INexaModuleBridge
{
    Task<bool> IsUsableAsync(
        string recipeDefId,
        int? recipeDefVersion,
        string equipmentClassId,
        CancellationToken ct = default);
}
