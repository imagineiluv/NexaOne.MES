using NexaOne.Common;

namespace NexaOne.ServiceContracts.Est;

/// <summary>복잡 서비스 얇은 브리지(ADR-008) — EST 설비상태. plugin(EST)이 구현하고 호스트가 GetBean→캐스트로
/// Default-ALC DI에 등록한다. Result&lt;T&gt;로 도메인 분기(Conflict/InvalidTransition/Validation/Success)를
/// 손실 없이 전달해 컨트롤러가 409/400/200으로 매핑한다.</summary>
public interface IEquipmentStateBridge
{
    Task<IReadOnlyList<EquipmentStateMatrixDto>> GetMatrixAsync(string plantId, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(string plantId, string fromState, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateDto>> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default);
    Task<Result<EquipmentStateDto>> ChangeStateAsync(string equipmentId, string plantId, string toState,
        string requestedBy, string? reason, string sourceType, int? expectedVersion, CancellationToken ct = default);
    Task<IReadOnlyList<EquipmentStateHistoryDto>> GetHistoryAsync(string equipmentId, int limit = 50, CancellationToken ct = default);
    Task<Result<EquipmentStateMatrixDto>> UpsertMatrixAsync(string plantId, string fromStateId, string toStateId,
        bool allowFlag, string? setStateId, bool requireReason, CancellationToken ct = default);
}
