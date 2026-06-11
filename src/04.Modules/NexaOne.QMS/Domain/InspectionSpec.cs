using NexaOne.Common;

namespace NexaOne.QMS.Domain;

public sealed class InspectionSpec : AuditableEntity<string>
{
    private static readonly HashSet<string> ValidMeasureTypes = ["Numeric", "Attribute"];

    private InspectionSpec(string specId) : base(specId) { }

    public string SpecName { get; private set; } = string.Empty;
    public string ProcessId { get; private set; } = string.Empty;
    public string ItemName { get; private set; } = string.Empty;
    public string MeasureType { get; private set; } = string.Empty;
    public decimal? NominalValue { get; private set; }
    public decimal? TolerancePlus { get; private set; }
    public decimal? ToleranceMinus { get; private set; }
    public bool IsActive { get; private set; }

    public static Result<InspectionSpec> Create(
        string specId,
        string specName,
        string processId,
        string itemName,
        string measureType,
        decimal? nominalValue = null,
        decimal? tolerancePlus = null,
        decimal? toleranceMinus = null)
    {
        if (string.IsNullOrWhiteSpace(specId))
            return Result.Failure<InspectionSpec>(Error.Validation(nameof(specId), "Spec ID is required."));
        if (string.IsNullOrWhiteSpace(specName))
            return Result.Failure<InspectionSpec>(Error.Validation(nameof(specName), "Spec name is required."));
        if (string.IsNullOrWhiteSpace(processId))
            return Result.Failure<InspectionSpec>(Error.Validation(nameof(processId), "Process ID is required."));
        if (string.IsNullOrWhiteSpace(itemName))
            return Result.Failure<InspectionSpec>(Error.Validation(nameof(itemName), "Item name is required."));
        if (!ValidMeasureTypes.Contains(measureType))
            return Result.Failure<InspectionSpec>(Error.Validation(nameof(measureType), "Measure type must be 'Numeric' or 'Attribute'."));

        var spec = new InspectionSpec(specId)
        {
            SpecName = specName,
            ProcessId = processId,
            ItemName = itemName,
            MeasureType = measureType,
            NominalValue = nominalValue,
            TolerancePlus = tolerancePlus,
            ToleranceMinus = toleranceMinus,
            IsActive = true
        };
        return spec;
    }
}
