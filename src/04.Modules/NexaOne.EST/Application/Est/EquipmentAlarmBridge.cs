using NexaOne.Common;
using NexaOne.EST.Domain;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Application.Est;

/// <summary>ADR-008 얇은 브리지 어댑터 — EquipmentAlarmService에 위임하고 도메인 엔티티를 계약 DTO로 매핑한다.
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IEquipmentAlarmBridge로 캐스트해 DI에 등록한다.
/// EquipmentStateBridge와 동일 패턴.</summary>
public sealed class EquipmentAlarmBridge : IEquipmentAlarmBridge
{
    private readonly EquipmentAlarmService _service;

    public EquipmentAlarmBridge(EquipmentAlarmService service) => _service = service;

    public async Task<Result<EquipmentAlarmDto>> RecordAlarmAsync(
        string alarmId, string equipmentId, string alarmCode, string alarmName, string level, CancellationToken ct = default)
    {
        var r = await _service.RecordAlarmAsync(alarmId, equipmentId, alarmCode, alarmName, level, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<EquipmentAlarmDto>(r.Error);
    }

    public Task<Result> ClearAlarmAsync(string alarmId, DateTime clearedAt, CancellationToken ct = default)
        => _service.ClearAlarmAsync(alarmId, clearedAt, ct);

    public async Task<IReadOnlyList<EquipmentAlarmDto>> GetActiveAlarmsAsync(string plantId, CancellationToken ct = default)
    {
        var r = await _service.GetActiveAlarmsAsync(plantId, ct);
        return r.IsSuccess ? r.Value.Select(ToDto).ToList() : Array.Empty<EquipmentAlarmDto>();
    }

    public Task<int> GetActiveAlarmCountAsync(CancellationToken ct = default)
        => _service.GetActiveAlarmCountAsync(ct);

    private static EquipmentAlarmDto ToDto(EquipmentAlarm a)
        => new(a.Id, a.EquipmentId, a.AlarmCode, a.AlarmName, a.AlarmLevel,
            a.OccurredAt, a.ClearedAt, a.ElapsedSeconds, a.IsActive);
}
