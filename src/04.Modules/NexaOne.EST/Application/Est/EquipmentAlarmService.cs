using NexaOne.EST.Domain;
using NexaOne.Common;
using NexaOne.ServiceContracts.Mdm;

namespace NexaOne.EST.Application.Est;

public sealed class EquipmentAlarmService
{
    private readonly IEquipmentAlarmRepository _alarmRepository;
    private readonly IEquipmentDirectory _equipmentDirectory;

    public EquipmentAlarmService(IEquipmentAlarmRepository alarmRepository, IEquipmentDirectory equipmentDirectory)
    {
        _alarmRepository = alarmRepository;
        _equipmentDirectory = equipmentDirectory;
    }

    public async Task<Result<EquipmentAlarm>> RecordAlarmAsync(
        string alarmId,
        string equipmentId,
        string alarmCode,
        string alarmName,
        string level,
        CancellationToken ct = default)
    {
        var result = EquipmentAlarm.Create(alarmId, equipmentId, alarmCode, alarmName, level, DateTime.UtcNow);
        if (result.IsFailure) return result;

        await _alarmRepository.AddAsync(result.Value, ct);
        return result;
    }

    public async Task<Result> ClearAlarmAsync(string alarmId, DateTime clearedAt, CancellationToken ct = default)
    {
        var alarm = await _alarmRepository.GetByIdAsync(alarmId, ct);
        if (alarm is null)
            return Result.Failure(Error.NotFound(nameof(EquipmentAlarm), $"EquipmentAlarm '{alarmId}'을(를) 찾을 수 없습니다."));

        alarm.Clear(clearedAt);
        await _alarmRepository.UpdateAsync(alarm, ct);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<EquipmentAlarm>>> GetActiveAlarmsAsync(string plantId, CancellationToken ct = default)
    {
        // ADR-006: MDM 스키마를 EST SQL에 박지 않고 호스트 IEquipmentDirectory로 plantId→설비 ID를 푼다.
        // 빈 목록이면 IN @...에 넘기지 않도록 단락한다(Dapper가 잘못된 SQL/throw 유발).
        var equipmentIds = await _equipmentDirectory.GetEquipmentIdsByPlantAsync(plantId, ct);
        if (equipmentIds.Count == 0)
            return Result.Success<IReadOnlyList<EquipmentAlarm>>(Array.Empty<EquipmentAlarm>());

        var alarms = await _alarmRepository.GetActiveAlarmsByEquipmentIdsAsync(equipmentIds, ct);
        return Result.Success(alarms);
    }

    public async Task<int> GetActiveAlarmCountAsync(CancellationToken ct = default)
        => await _alarmRepository.GetActiveAlarmCountAsync(ct);
}
