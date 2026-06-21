using NexaOne.Common;

namespace NexaOne.ServiceContracts.Est;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — EST 설비알람. plugin(EST)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. 쓰기(Record/Clear)는 도메인 전이·도메인이벤트를 Result로 손실 없이 전달해
/// 컨트롤러가 404/400/200으로 매핑하고, 조회(활성알람/카운트)는 읽기 전용이다. EquipmentStateBridge와 동일 패턴.</summary>
public interface IEquipmentAlarmBridge
{
    Task<Result<EquipmentAlarmDto>> RecordAlarmAsync(
        string alarmId, string equipmentId, string alarmCode, string alarmName, string level, CancellationToken ct = default);
    Task<Result> ClearAlarmAsync(string alarmId, DateTime clearedAt, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentAlarmDto>> GetActiveAlarmsAsync(string plantId, CancellationToken ct = default);
    Task<int> GetActiveAlarmCountAsync(CancellationToken ct = default);
}
