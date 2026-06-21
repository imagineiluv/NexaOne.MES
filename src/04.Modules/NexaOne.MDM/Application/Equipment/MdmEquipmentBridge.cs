using NexaOne.Common;
using NexaOne.MDM.Domain;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.MDM.Application.Equipments;

/// <summary>ADR-008 얇은 브리지 어댑터 — EquipmentService에 위임하고 Equipment를 계약 DTO로 매핑.
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IMdmEquipmentBridge로 캐스트해 DI에 등록한다.
/// 도메인 불변식이 있는 생성/비활성(Valid→Invalid)/갱신만 노출한다(순수 조회는 게이트웨이).</summary>
public sealed class MdmEquipmentBridge : IMdmEquipmentBridge
{
    private readonly EquipmentService _service;
    public MdmEquipmentBridge(EquipmentService service) => _service = service;

    public async Task<Result<EquipmentDto>> CreateEquipmentAsync(
        string equipmentId, string equipmentName, string plantId, string areaId, string equipmentType,
        string? parentEquipmentId = null, string vendor = "", string model = "", string equipmentClassId = "",
        CancellationToken ct = default)
    {
        var r = await _service.CreateEquipmentAsync(
            equipmentId, equipmentName, plantId, areaId, equipmentType, parentEquipmentId, vendor, model, equipmentClassId, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<EquipmentDto>(r.Error);
    }

    public Task<Result> DeactivateEquipmentAsync(string equipmentId, CancellationToken ct = default)
        => _service.DeactivateEquipmentAsync(equipmentId, ct);

    public async Task<Result<EquipmentDto>> UpdateEquipmentAsync(
        string equipmentId, string name, string description, string equipmentType, string vendor, string model,
        CancellationToken ct = default)
    {
        var r = await _service.UpdateEquipmentAsync(equipmentId, name, description, equipmentType, vendor, model, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<EquipmentDto>(r.Error);
    }

    private static EquipmentDto ToDto(Equipment e)
        => new(e.Id, e.EquipmentName, e.Description, e.PlantId, e.AreaId, e.EquipmentType,
            e.ParentEquipmentId, e.Vendor, e.Model, e.EquipmentClassId, e.ValidState);
}
