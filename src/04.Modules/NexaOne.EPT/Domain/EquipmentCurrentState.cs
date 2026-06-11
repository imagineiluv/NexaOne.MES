using NexaOne.Common;

namespace NexaOne.EPT.Domain;

/// <summary>Current operational state of a single equipment.</summary>
public sealed class EquipmentCurrentState : Entity<string>
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
    }
}
