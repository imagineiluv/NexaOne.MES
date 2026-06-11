using NexaOne.EPT.Domain;
using NexaOne.Common;

namespace NexaOne.EPT.Application.Ept;

public sealed class EquipmentAlarmService
{
    private readonly IEquipmentAlarmRepository _alarmRepository;

    public EquipmentAlarmService(IEquipmentAlarmRepository alarmRepository)
    {
        _alarmRepository = alarmRepository;
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
            return Result.Failure(Error.NotFound(nameof(EquipmentAlarm), alarmId));

        alarm.Clear(clearedAt);
        await _alarmRepository.UpdateAsync(alarm, ct);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<EquipmentAlarm>>> GetActiveAlarmsAsync(string plantId, CancellationToken ct = default)
    {
        var alarms = await _alarmRepository.GetActiveAlarmsAsync(plantId, ct);
        return Result.Success(alarms);
    }

    public async Task<int> GetActiveAlarmCountAsync(CancellationToken ct = default)
        => await _alarmRepository.GetActiveAlarmCountAsync(ct);
}
