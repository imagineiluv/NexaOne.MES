using NexaOne.Common;

namespace NexaOne.EST.Domain;

public sealed class EquipmentStateMatrix : Entity<string>
{
    private EquipmentStateMatrix(string id) : base(id) { }

    public string PlantId { get; private set; } = string.Empty;
    public string FromStateId { get; private set; } = string.Empty;
    public string ToStateId { get; private set; } = string.Empty;
    public bool AllowFlag { get; private set; }
    /// <summary>Actual state applied after transition. Equals ToStateId if no override.</summary>
    public string SetStateId { get; private set; } = string.Empty;
    public bool RequireReason { get; private set; }
    public string ValidState { get; private set; } = "Valid";

    public static EquipmentStateMatrix Create(
        string plantId,
        string fromStateId,
        string toStateId,
        bool allowFlag,
        string? setStateId = null,
        bool requireReason = false)
    {
        var id = $"{plantId}:{fromStateId}:{toStateId}";
        return new EquipmentStateMatrix(id)
        {
            PlantId       = plantId,
            FromStateId   = fromStateId,
            ToStateId     = toStateId,
            AllowFlag     = allowFlag,
            SetStateId    = string.IsNullOrEmpty(setStateId) ? toStateId : setStateId,
            RequireReason = requireReason,
            ValidState    = "Valid"
        };
    }

    public void Update(bool allowFlag, string? setStateId, bool requireReason)
    {
        AllowFlag     = allowFlag;
        SetStateId    = string.IsNullOrEmpty(setStateId) ? ToStateId : setStateId;
        RequireReason = requireReason;
    }

    public void Invalidate() => ValidState = "Invalid";
}
