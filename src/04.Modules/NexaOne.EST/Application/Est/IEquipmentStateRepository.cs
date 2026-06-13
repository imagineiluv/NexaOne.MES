using NexaOne.EST.Domain;

namespace NexaOne.EST.Application.Est;

public interface IEquipmentStateRepository
{
    Task<EquipmentCurrentState?> GetAsync(string equipmentId, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentCurrentState>> GetByPlantAsync(string plantId, CancellationToken ct = default);
    Task UpsertAsync(EquipmentCurrentState state, CancellationToken ct = default);
    Task AddHistoryAsync(EquipmentStateHistory history, CancellationToken ct = default);

    /// <summary>현재 상태 갱신(업서트)과 이력 기록을 단일 트랜잭션으로 원자적으로 수행한다.
    /// 상태만 바뀌고 이력이 누락되는(또는 그 반대) 부분 커밋을 방지한다.</summary>
    Task ChangeStateWithHistoryAsync(
        EquipmentCurrentState state, EquipmentStateHistory history, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateHistory>> GetHistoryAsync(string equipmentId, int limit = 50, CancellationToken ct = default);
}
