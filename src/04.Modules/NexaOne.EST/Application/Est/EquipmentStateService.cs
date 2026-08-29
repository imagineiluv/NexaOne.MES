using NexaOne.Common;
using NexaOne.EST.Domain;

namespace NexaOne.EST.Application.Est;

public sealed class EquipmentStateService
{
    private readonly IEquipmentStateMatrixRepository _matrixRepo;
    private readonly IEquipmentStateRepository _stateRepo;

    public EquipmentStateService(
        IEquipmentStateMatrixRepository matrixRepo,
        IEquipmentStateRepository stateRepo)
    {
        _matrixRepo = matrixRepo;
        _stateRepo  = stateRepo;
    }

    public Task<IReadOnlyList<EquipmentStateMatrix>> GetMatrixAsync(
        string plantId, CancellationToken ct = default)
        => _matrixRepo.GetByPlantAsync(plantId, ct);

    public Task<IReadOnlyList<EquipmentStateMatrix>> GetAllowedTransitionsAsync(
        string plantId, string fromState, CancellationToken ct = default)
        => _matrixRepo.GetAllowedTransitionsAsync(plantId, fromState, ct);

    public Task<IReadOnlyList<EquipmentCurrentState>> GetEquipmentStatesAsync(
        string plantId, CancellationToken ct = default)
        => _stateRepo.GetByPlantAsync(plantId, ct);

    public async Task<Result<EquipmentCurrentState>> ChangeStateAsync(
        string equipmentId,
        string plantId,
        string toState,
        string requestedBy,
        string reason = "",
        string sourceType = "UI",
        int? expectedVersion = null,
        CancellationToken ct = default)
    {
        var current = await _stateRepo.GetAsync(equipmentId, ct);

        // Bootstrap: if no state record exists, create one with IDLE as initial state
        if (current is null)
        {
            var initial = EquipmentCurrentState.Create(equipmentId, plantId, "IDLE");
            if (await _stateRepo.TryInitializeAsync(initial, ct))
            {
                current = initial;
            }
            else
            {
                // 다른 요청이 최초 상태를 먼저 만들었다. 그 승자 상태를 다시 읽어야 하며,
                // Upsert로 덮어쓰면 이미 진행된 전이를 IDLE/version 1로 되돌릴 수 있다.
                current = await _stateRepo.GetAsync(equipmentId, ct);
                if (current is null)
                    return Result.Failure<EquipmentCurrentState>(Error.Conflict(
                        "EST.EquipmentState.InitializationRace",
                        "Equipment state initialization raced with another request; reload and retry."));
            }
        }

        if (!current.PlantId.Equals(plantId, StringComparison.OrdinalIgnoreCase))
            return Result.Failure<EquipmentCurrentState>(Error.Conflict(
                "EST.EquipmentState.PlantMismatch",
                $"Equipment '{equipmentId}' belongs to plant '{current.PlantId}', not '{plantId}'."));

        // Optimistic concurrency check
        if (expectedVersion.HasValue && current.StateVersion != expectedVersion.Value)
            return Result.Failure<EquipmentCurrentState>(
                Error.Conflict($"Equipment state was changed concurrently. Current version: {current.StateVersion}."));

        var fromState = current.CurrentStateId;
        var persistedVersion = current.StateVersion;

        // Matrix validation
        var matrix = await _matrixRepo.FindAsync(plantId, fromState, toState, ct);
        if (matrix is null || !matrix.AllowFlag)
            return Result.Failure<EquipmentCurrentState>(
                Error.Failure("EPT.InvalidTransition",
                    $"Transition {fromState} → {toState} is not allowed for plant {plantId}."));

        if (matrix.RequireReason && string.IsNullOrWhiteSpace(reason))
            return Result.Failure<EquipmentCurrentState>(
                Error.Validation("reason", "A reason is required for this state transition."));

        var setState = matrix.SetStateId;

        // Apply transition
        current.ApplyTransition(setState);

        // Record history — 상태 변경과 이력 기록은 원자적으로 묶는다(상태만 바뀌고 이력이 누락되는 부분 커밋 방지).
        var histId = $"{equipmentId}_{DateTime.UtcNow:yyyyMMddHHmmssfff}";
        var histResult = EquipmentStateHistory.Create(
            histId, equipmentId, fromState, toState, setState,
            current.StateChangedAt, requestedBy, reason, sourceType);

        if (histResult.IsFailure)
            return Result.Failure<EquipmentCurrentState>(histResult.Error);

        if (!await _stateRepo.TryChangeStateWithHistoryAsync(
                current, histResult.Value, persistedVersion, ct))
        {
            var winner = await _stateRepo.GetAsync(equipmentId, ct);
            return Result.Failure<EquipmentCurrentState>(Error.Conflict(
                "EST.EquipmentState.ConcurrentChange",
                $"Equipment state was changed concurrently. Current version: {winner?.StateVersion}."));
        }

        return Result.Success(current);
    }

    public Task<IReadOnlyList<EquipmentStateHistory>> GetHistoryAsync(
        string equipmentId, int limit = 50, CancellationToken ct = default)
        => _stateRepo.GetHistoryAsync(equipmentId, limit, ct);

    public async Task<Result<EquipmentStateMatrix>> UpsertMatrixAsync(
        string plantId,
        string fromStateId,
        string toStateId,
        bool allowFlag,
        string? setStateId,
        bool requireReason,
        CancellationToken ct = default)
    {
        var existing = await _matrixRepo.FindAsync(plantId, fromStateId, toStateId, ct);
        if (existing is not null)
        {
            existing.Update(allowFlag, setStateId, requireReason);
            await _matrixRepo.UpdateAsync(existing, ct);
            return Result.Success(existing);
        }

        var matrix = EquipmentStateMatrix.Create(plantId, fromStateId, toStateId, allowFlag, setStateId, requireReason);
        await _matrixRepo.AddAsync(matrix, ct);
        return Result.Success(matrix);
    }
}
