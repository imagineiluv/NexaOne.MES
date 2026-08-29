using NexaOne.ServiceContracts.Mdm;
using NexaOne.ServiceContracts.Pom;
using NexaOne.ServiceContracts.Qms;
using NexaOne.ServiceContracts.Rms;

namespace NexaOne.Server.Gateway;

/// <summary>
/// POM의 단일 추적 마스터 인터페이스를 MDM·RMS·QMS owner directory로 조합하는 호스트 adapter입니다.
/// 물리 SQL과 상태 어휘의 해석은 각 소유 모듈에 남고, 이 adapter는 DTO 변환과 호출 순서만 소유합니다.
/// </summary>
public sealed class TrackingMasterGateway : ITrackingMasterGateway
{
    private readonly IEquipmentDirectory _equipmentDirectory;
    private readonly ITrackingRoutingDirectory _routingDirectory;
    private readonly ITrackingRecipeDirectory _recipeDirectory;
    private readonly ITrackingDefectDirectory _defectDirectory;

    public TrackingMasterGateway(
        IEquipmentDirectory equipmentDirectory,
        ITrackingRoutingDirectory routingDirectory,
        ITrackingRecipeDirectory recipeDirectory,
        ITrackingDefectDirectory defectDirectory)
    {
        _equipmentDirectory = equipmentDirectory ?? throw new ArgumentNullException(nameof(equipmentDirectory));
        _routingDirectory = routingDirectory ?? throw new ArgumentNullException(nameof(routingDirectory));
        _recipeDirectory = recipeDirectory ?? throw new ArgumentNullException(nameof(recipeDirectory));
        _defectDirectory = defectDirectory ?? throw new ArgumentNullException(nameof(defectDirectory));
    }

    public async Task<TrackingEquipmentInfo?> GetEquipmentAsync(
        string equipmentId,
        CancellationToken ct = default)
    {
        var equipment = await _equipmentDirectory.GetEquipmentAsync(equipmentId, ct);
        return equipment is null
            ? null
            : new TrackingEquipmentInfo(
                equipment.EquipmentId,
                equipment.PlantId,
                equipment.EquipmentClassId,
                equipment.IsValid);
    }

    public Task<TrackingProductRouting?> GetProductRoutingAsync(
        string routingId,
        CancellationToken ct = default)
        => _routingDirectory.GetProductRoutingAsync(routingId, ct);

    public Task<bool> IsUsableRecipeAsync(
        string recipeDefId,
        int? recipeDefVersion,
        string equipmentClassId,
        CancellationToken ct = default)
        => _recipeDirectory.IsUsableAsync(
            recipeDefId,
            recipeDefVersion,
            equipmentClassId,
            ct);

    public Task<bool> IsValidDefectCodeAsync(
        string defectCode,
        CancellationToken ct = default)
        => _defectDirectory.IsValidAsync(defectCode, ct);
}
