namespace NexaOne.FDC.Application.Fdc;

/// <summary>Transport-neutral quality used by the FDC collection use case.</summary>
public enum FdcSampleQuality
{
    Good,
    Uncertain,
    Bad,
}

/// <summary>
/// A normalized sample entering the FDC application boundary. PLC protocol values and quality
/// codes are converted by the infrastructure adapter before this record is created.
/// </summary>
public sealed record FdcTagSample
{
    public FdcTagSample(string parameterId, decimal value, FdcSampleQuality quality)
    {
        if (string.IsNullOrWhiteSpace(parameterId))
            throw new ArgumentException("Parameter id is required.", nameof(parameterId));
        if (!Enum.IsDefined(quality))
            throw new ArgumentOutOfRangeException(nameof(quality));

        ParameterId = parameterId;
        Value = value;
        Quality = quality;
    }

    public string ParameterId { get; }
    public decimal Value { get; }
    public FdcSampleQuality Quality { get; }
}
