using NexaOne.Common;

namespace NexaOne.QMS.Domain;

public sealed class InspectionResult : AuditableEntity<string>
{
    private InspectionResult(string resultId) : base(resultId) { }

    public string SpecId { get; private set; } = string.Empty;
    public string LotId { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public decimal? MeasuredValue { get; private set; }
    public string? AttributeResult { get; private set; }
    public DateTime InspectedAt { get; private set; }
    public string InspectorId { get; private set; } = string.Empty;
    public bool IsPass { get; private set; }
    public string? Remark { get; private set; }

    public static Result<InspectionResult> Create(
        string resultId,
        string specId,
        string lotId,
        string equipmentId,
        DateTime inspectedAt,
        string inspectorId,
        decimal? measuredValue = null,
        string? attributeResult = null,
        bool? isPass = null,
        decimal? nominalValue = null,
        decimal? tolerancePlus = null,
        decimal? toleranceMinus = null,
        string? measureType = null,
        string? remark = null)
    {
        if (string.IsNullOrWhiteSpace(resultId))
            return Result.Failure<InspectionResult>(Error.Validation(nameof(resultId), "Result ID is required."));
        if (string.IsNullOrWhiteSpace(specId))
            return Result.Failure<InspectionResult>(Error.Validation(nameof(specId), "Spec ID is required."));
        if (string.IsNullOrWhiteSpace(lotId))
            return Result.Failure<InspectionResult>(Error.Validation(nameof(lotId), "Lot ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<InspectionResult>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(inspectorId))
            return Result.Failure<InspectionResult>(Error.Validation(nameof(inspectorId), "Inspector ID is required."));

        bool computedIsPass;
        if (measureType == "Numeric" && measuredValue.HasValue && nominalValue.HasValue)
        {
            var upper = nominalValue.Value + (tolerancePlus ?? decimal.MaxValue);
            var lower = nominalValue.Value - (toleranceMinus ?? decimal.MaxValue);
            computedIsPass = measuredValue.Value >= lower && measuredValue.Value <= upper;
        }
        else
        {
            computedIsPass = isPass ?? false;
        }

        var result = new InspectionResult(resultId)
        {
            SpecId = specId,
            LotId = lotId,
            EquipmentId = equipmentId,
            MeasuredValue = measuredValue,
            AttributeResult = attributeResult,
            InspectedAt = inspectedAt,
            InspectorId = inspectorId,
            IsPass = computedIsPass,
            Remark = remark
        };
        return result;
    }
}
