using NexaOne.MDM.Application.Equipments;
using NexaOne.PPM.Application.Lots;
using NexaOne.QMS.Application.Qms;
using NexaOne.RMS.Application.Rms;
using NexaOne.RMS.Domain;

namespace NexaOne.API.Services;

/// <summary>
/// PPM ITrackingMasterGateway의 API 조립 계층 어댑터 (설계 19.4.2).
/// PPM 모듈은 MDM/RMS/QMS를 직접 참조하지 않으므로 여기서 교차 모듈 조회를 연결한다.
/// </summary>
public sealed class TrackingMasterGateway(
    IEquipmentRepository equipments,
    IRecipeRepository recipes,
    IDefectClassRepository defectClasses) : ITrackingMasterGateway
{
    public async Task<TrackingEquipmentInfo?> GetEquipmentAsync(
        string equipmentId, CancellationToken ct = default)
    {
        var equipment = await equipments.GetByIdAsync(equipmentId, ct);
        return equipment is null
            ? null
            : new TrackingEquipmentInfo(
                equipment.Id, equipment.PlantId, equipment.EquipmentClassId,
                IsValid: equipment.ValidState == "Valid");
    }

    public async Task<bool> IsUsableRecipeAsync(
        string recipeDefId, int? recipeDefVersion, string equipmentClassId, CancellationToken ct = default)
    {
        var recipe = await recipes.GetByIdAsync(recipeDefId, ct);
        if (recipe is null) return false;
        if (recipe.ApprovalState != RecipeApprovalState.Released) return false;
        if (!string.Equals(recipe.EquipmentClassId, equipmentClassId, StringComparison.OrdinalIgnoreCase))
            return false;
        // 버전 미지정은 현재 배포 버전 사용으로 허용 (현행 setIsUseValidationRecipe(false) 적응)
        return !recipeDefVersion.HasValue || recipe.Version == recipeDefVersion.Value;
    }

    public async Task<bool> IsValidDefectCodeAsync(string defectCode, CancellationToken ct = default)
    {
        var defectClass = await defectClasses.GetByIdAsync(defectCode, ct);
        return defectClass is not null && defectClass.IsActive;
    }
}
