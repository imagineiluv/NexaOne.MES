using NexaOne.MDM.Domain;
using NexaOne.Common;

namespace NexaOne.MDM.Application.Equipments;

public sealed class EquipmentService
{
    private readonly IEquipmentRepository _equipmentRepository;

    public EquipmentService(IEquipmentRepository equipmentRepository)
    {
        _equipmentRepository = equipmentRepository;
    }

    public async Task<Result<Equipment>> CreateEquipmentAsync(
        string equipmentId,
        string equipmentName,
        string plantId,
        string areaId,
        string equipmentType,
        string? parentEquipmentId = null,
        string vendor = "",
        string model = "",
        string equipmentClassId = "",
        CancellationToken ct = default)
    {
        if (await _equipmentRepository.ExistsAsync(equipmentId, ct))
            return Result.Failure<Equipment>(Error.Conflict($"Equipment '{equipmentId}' already exists."));

        var result = Equipment.Create(equipmentId, equipmentName, plantId, areaId, equipmentType, parentEquipmentId, vendor, model, equipmentClassId);
        if (result.IsFailure) return result;

        await _equipmentRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result<Equipment>> GetEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        var equipment = await _equipmentRepository.GetByIdAsync(equipmentId, ct);
        return equipment is null
            ? Result.Failure<Equipment>(Error.NotFoundOf(nameof(Equipment), equipmentId))
            : Result.Success(equipment);
    }

    public async Task<Result<IReadOnlyList<Equipment>>> GetEquipmentListAsync(string plantId, CancellationToken ct = default)
    {
        var list = await _equipmentRepository.GetAllByPlantAsync(plantId, ct);
        return Result.Success(list);
    }

    public async Task<Result> DeactivateEquipmentAsync(string equipmentId, CancellationToken ct = default)
    {
        var equipment = await _equipmentRepository.GetByIdAsync(equipmentId, ct);
        if (equipment is null)
            return Result.Failure(Error.NotFoundOf(nameof(Equipment), equipmentId));

        equipment.Deactivate();
        await _equipmentRepository.UpdateAsync(equipment, ct);
        return Result.Success();
    }

    public async Task<Result<Equipment>> UpdateEquipmentAsync(
        string equipmentId,
        string name,
        string description,
        string equipmentType,
        string vendor,
        string model,
        CancellationToken ct = default)
    {
        var equipment = await _equipmentRepository.GetByIdAsync(equipmentId, ct);
        if (equipment is null)
            return Result.Failure<Equipment>(Error.NotFoundOf(nameof(Equipment), equipmentId));

        equipment.UpdateInfo(name, description, equipmentType, vendor, model);
        await _equipmentRepository.UpdateAsync(equipment, ct);
        return Result.Success(equipment);
    }
}
