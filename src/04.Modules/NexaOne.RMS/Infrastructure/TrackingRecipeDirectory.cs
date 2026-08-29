using NexaOne.Infrastructure.Persistence;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.RMS.Infrastructure;

/// <summary>Released Recipe의 설비 분류·버전 적합성을 제공하는 RMS owner adapter입니다.</summary>
public sealed class TrackingRecipeDirectory : QueryRepository, ITrackingRecipeDirectory
{
    public TrackingRecipeDirectory(EesDataSource dataSource) : base(dataSource) { }

    public async Task<bool> IsUsableAsync(
        string recipeDefId,
        int? recipeDefVersion,
        string equipmentClassId,
        CancellationToken ct = default)
        => await CountAsync(
            @"SELECT COUNT(*) FROM RMS_RECIPE
              WHERE RECIPE_ID = @recipeDefId
                AND APPROVAL_STATE = 'Released'
                AND EQUIPMENT_CLASS_ID = @equipmentClassId
                AND (@recipeDefVersion IS NULL OR VERSION = @recipeDefVersion)",
            new { recipeDefId, recipeDefVersion, equipmentClassId },
            ct) > 0;
}
