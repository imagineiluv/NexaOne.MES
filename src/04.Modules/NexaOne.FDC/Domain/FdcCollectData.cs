using NexaOne.Common;

namespace NexaOne.FDC.Domain;

public sealed class FdcCollectData : Entity<string>
{
    private FdcCollectData(string id) : base(id) { }

    public string EquipmentId { get; private set; } = string.Empty;
    public string ParameterId { get; private set; } = string.Empty;
    public decimal Value { get; private set; }
    public DateTime CollectedAt { get; private set; }
    public string Quality { get; private set; } = string.Empty;
    public decimal LowerLimit { get; private set; }
    public decimal UpperLimit { get; private set; }
    public bool IsOutOfSpec => Value < LowerLimit || Value > UpperLimit;

    public static Result<FdcCollectData> Create(
        string id,
        string equipmentId,
        string parameterId,
        decimal value,
        DateTime collectedAt,
        string quality,
        decimal lowerLimit,
        decimal upperLimit)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Result.Failure<FdcCollectData>(Error.Validation(nameof(id), "Collect data ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<FdcCollectData>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(parameterId))
            return Result.Failure<FdcCollectData>(Error.Validation(nameof(parameterId), "Parameter ID is required."));
        if (quality is not ("Good" or "Bad" or "Uncertain"))
            return Result.Failure<FdcCollectData>(Error.Validation(nameof(quality), "Quality must be Good, Bad, or Uncertain."));

        var data = new FdcCollectData(id)
        {
            EquipmentId = equipmentId,
            ParameterId = parameterId,
            Value = value,
            CollectedAt = collectedAt,
            Quality = quality,
            LowerLimit = lowerLimit,
            UpperLimit = upperLimit
        };
        return data;
    }
}
