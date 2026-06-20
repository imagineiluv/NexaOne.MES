using NexaOne.Common;
using NexaOne.EST.Domain;
using NexaOne.ServiceContracts.Est;

namespace NexaOne.EST.Application.Est;

/// <summary>ADR-008 얇은 브리지 어댑터 — EquipmentStateService에 위임하고 도메인 엔티티를 계약 DTO로 매핑한다.
/// plugin ALC에서 생성되며 호스트(Default ALC)가 IEquipmentStateBridge로 캐스트해 DI에 등록한다.</summary>
public sealed class EquipmentStateBridge : IEquipmentStateBridge
{
    private readonly EquipmentStateService _service;

    public EquipmentStateBridge(EquipmentStateService service) => _service = service;

    public async Task<IReadOnlyList<EquipmentStateMatrixDto>> GetMatrixAsync(string plantId, CancellationToken ct = default)
        => (await _service.GetMatrixAsync(plantId, ct)).Select(ToDto).ToList();

    public async Task<IReadOnlyList<EquipmentStateMatrixDto>> GetAllowedTransitionsAsync(
        string plantId, string fromState, CancellationToken ct = default)
        => (await _service.GetAllowedTransitionsAsync(plantId, fromState, ct)).Select(ToDto).ToList();

    public async Task<IReadOnlyList<EquipmentStateDto>> GetEquipmentStatesAsync(string plantId, CancellationToken ct = default)
        => (await _service.GetEquipmentStatesAsync(plantId, ct)).Select(ToDto).ToList();

    public async Task<Result<EquipmentStateDto>> ChangeStateAsync(string equipmentId, string plantId, string toState,
        string requestedBy, string? reason, string sourceType, int? expectedVersion, CancellationToken ct = default)
    {
        var r = await _service.ChangeStateAsync(
            equipmentId, plantId, toState, requestedBy, reason ?? string.Empty, sourceType, expectedVersion, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<EquipmentStateDto>(r.Error);
    }

    public async Task<IReadOnlyList<EquipmentStateHistoryDto>> GetHistoryAsync(
        string equipmentId, int limit = 50, CancellationToken ct = default)
        => (await _service.GetHistoryAsync(equipmentId, limit, ct)).Select(ToDto).ToList();

    public async Task<Result<EquipmentStateMatrixDto>> UpsertMatrixAsync(string plantId, string fromStateId, string toStateId,
        bool allowFlag, string? setStateId, bool requireReason, CancellationToken ct = default)
    {
        var r = await _service.UpsertMatrixAsync(plantId, fromStateId, toStateId, allowFlag, setStateId, requireReason, ct);
        return r.IsSuccess ? Result.Success(ToDto(r.Value)) : Result.Failure<EquipmentStateMatrixDto>(r.Error);
    }

    private static EquipmentStateDto ToDto(EquipmentCurrentState e)
        => new(e.Id, e.PlantId, e.CurrentStateId, e.StateChangedAt, e.StateVersion);

    private static EquipmentStateMatrixDto ToDto(EquipmentStateMatrix m)
        => new(m.Id, m.PlantId, m.FromStateId, m.ToStateId, m.AllowFlag, m.SetStateId, m.RequireReason, m.ValidState);

    private static EquipmentStateHistoryDto ToDto(EquipmentStateHistory h)
        => new(h.Id, h.EquipmentId, h.FromState, h.ToState, h.SetState, h.ChangedAt, h.ChangedBy, h.Reason, h.SourceType, h.DurationSeconds);
}
