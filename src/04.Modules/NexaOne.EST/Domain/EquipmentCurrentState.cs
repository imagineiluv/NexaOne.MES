using NexaOne.Common;

namespace NexaOne.EST.Domain;

/// <summary>Current operational state of a single equipment. 상태 전이 시 도메인 이벤트를 발행한다(ADR-002).</summary>
public sealed class EquipmentCurrentState : AggregateRoot<string>
{
    private EquipmentCurrentState(string equipmentId) : base(equipmentId) { }

    public string PlantId { get; private set; } = string.Empty;
    public string CurrentStateId { get; private set; } = string.Empty;
    public DateTime StateChangedAt { get; private set; }
    /// <summary>Optimistic concurrency token.</summary>
    public int StateVersion { get; private set; }

    public static EquipmentCurrentState Create(
        string equipmentId,
        string plantId,
        string initialState = "IDLE")
    {
        return new EquipmentCurrentState(equipmentId)
        {
            PlantId        = plantId,
            CurrentStateId = initialState,
            StateChangedAt = DateTime.UtcNow,
            StateVersion   = 1
        };
    }

    public static EquipmentCurrentState Restore(
        string equipmentId,
        string plantId,
        string currentStateId,
        DateTime stateChangedAt,
        int stateVersion)
    {
        return new EquipmentCurrentState(equipmentId)
        {
            PlantId        = plantId,
            CurrentStateId = currentStateId,
            StateChangedAt = stateChangedAt,
            StateVersion   = stateVersion
        };
    }

    public void ApplyTransition(string newState)
    {
        CurrentStateId = newState;
        StateChangedAt = DateTime.UtcNow;
        StateVersion++;
        // ADR-002: 전이를 도메인 이벤트로 발행한다. 리포가 상태 변경과 동일 트랜잭션에 outbox로 기록한다.
        RaiseDomainEvent(new EquipmentStateChangedDomainEvent(Id, newState));
    }
}
