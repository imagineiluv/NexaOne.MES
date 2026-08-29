using NexaOne.Common;

namespace NexaOne.QMS.Domain;

/// <summary>검사가 발생한 업무 경계를 나타냅니다. 저장소와 화면이 같은 값을 사용해 수입·공정·출하 결과가 섞이지 않게 합니다.</summary>
public enum InspectionExecutionType
{
    Incoming,
    Process,
    Shipping
}

public sealed class InspectionResult : AuditableEntity<string>
{
    private InspectionResult(string resultId) : base(resultId) { }

    public string SpecId { get; private set; } = string.Empty;
    public string InspectionId { get; private set; } = string.Empty;
    public InspectionExecutionType InspectionType { get; private set; } = InspectionExecutionType.Process;
    public string LotId { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public decimal? MeasuredValue { get; private set; }
    public string? AttributeResult { get; private set; }
    public DateTime InspectedAt { get; private set; }
    public string InspectorId { get; private set; } = string.Empty;
    public bool IsPass { get; private set; }
    public string? Remark { get; private set; }
    public int SampleQuantity { get; private set; } = 1;
    public int DefectQuantity { get; private set; }

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
        string? remark = null,
        InspectionExecutionType inspectionType = InspectionExecutionType.Process,
        string? inspectionId = null,
        int sampleQuantity = 1,
        int? defectQuantity = null)
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
        if (!Enum.IsDefined(inspectionType))
            return Result.Failure<InspectionResult>(Error.Validation(nameof(inspectionType), "Inspection type must be Incoming, Process, or Shipping."));
        if (sampleQuantity <= 0)
            return Result.Failure<InspectionResult>(Error.Validation(nameof(sampleQuantity), "Item sample quantity must be positive."));

        var normalizedMeasureType = InspectionSpec.NormalizeMeasureType(measureType);
        if (normalizedMeasureType is null)
            return Result.Failure<InspectionResult>(Error.Validation(nameof(measureType), "A valid measure type is required."));

        bool computedIsPass;
        if (normalizedMeasureType == InspectionSpec.VariableMeasureType)
        {
            if (measuredValue is null || nominalValue is null)
                return Result.Failure<InspectionResult>(Error.Validation(nameof(measuredValue), "A variable inspection requires a measured and nominal value."));
            if (tolerancePlus < 0 || toleranceMinus < 0)
                return Result.Failure<InspectionResult>(Error.Validation(nameof(tolerancePlus), "Tolerances must be non-negative."));

            // Compare differences instead of adding decimal.MaxValue. This is overflow-free and
            // naturally supports a missing one-sided tolerance as an unbounded side.
            var upperPass = measuredValue.Value <= nominalValue.Value ||
                tolerancePlus is null || measuredValue.Value - nominalValue.Value <= tolerancePlus.Value;
            var lowerPass = measuredValue.Value >= nominalValue.Value ||
                toleranceMinus is null || nominalValue.Value - measuredValue.Value <= toleranceMinus.Value;
            computedIsPass = upperPass && lowerPass;
        }
        else
        {
            if (measuredValue is not null)
                return Result.Failure<InspectionResult>(Error.Validation(nameof(measuredValue), "An attribute inspection cannot contain a measured value."));
            var normalizedAttribute = NormalizeAttributeResult(attributeResult);
            if (normalizedAttribute is null)
                return Result.Failure<InspectionResult>(Error.Validation(nameof(attributeResult), "Attribute result must be 'Pass' or 'Fail'."));
            computedIsPass = normalizedAttribute == "Pass";
            if (isPass.HasValue && isPass.Value != computedIsPass)
                return Result.Failure<InspectionResult>(Error.Validation(nameof(isPass), "Client verdict conflicts with the server attribute verdict."));
            attributeResult = normalizedAttribute;
        }

        var normalizedDefectQuantity = defectQuantity ?? (computedIsPass ? 0 : 1);
        if (normalizedDefectQuantity < 0 || normalizedDefectQuantity > sampleQuantity)
            return Result.Failure<InspectionResult>(Error.Validation(
                nameof(defectQuantity), "Item defect quantity must be between zero and its sample quantity."));

        var result = new InspectionResult(resultId)
        {
            SpecId = specId,
            InspectionId = string.IsNullOrWhiteSpace(inspectionId) ? resultId : inspectionId.Trim(),
            InspectionType = inspectionType,
            LotId = lotId,
            EquipmentId = equipmentId,
            MeasuredValue = measuredValue,
            AttributeResult = attributeResult,
            InspectedAt = inspectedAt,
            InspectorId = inspectorId,
            IsPass = computedIsPass,
            Remark = remark,
            SampleQuantity = sampleQuantity,
            DefectQuantity = normalizedDefectQuantity
        };
        return result;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 저장된 IS_PASS를 권위값으로 받지 않고 nominalValue/measureType이 없는 읽기경로에서
    /// IsPass를 재계산(else 분기로 isPass ?? false)해, 검사 시점에 확정된 합부 판정이 읽기마다
    /// 다시 도출되는 상태손실을 막는다(스펙 공차 변경·수동 판정 시 합부가 조용히 뒤집힘).</summary>
    public static InspectionResult Restore(
        string resultId, string specId, string lotId, string equipmentId,
        decimal? measuredValue, string? attributeResult, DateTime inspectedAt,
        string inspectorId, bool isPass, string? remark,
        string? createdBy = null, DateTime? createdAt = null,
        string? updatedBy = null, DateTime? updatedAt = null,
        InspectionExecutionType inspectionType = InspectionExecutionType.Process,
        string? inspectionId = null,
        int sampleQuantity = 1,
        int defectQuantity = 0)
    {
        var result = new InspectionResult(resultId)
        {
            SpecId = specId,
            InspectionId = string.IsNullOrWhiteSpace(inspectionId) ? resultId : inspectionId,
            InspectionType = inspectionType,
            LotId = lotId,
            EquipmentId = equipmentId,
            MeasuredValue = measuredValue,
            AttributeResult = attributeResult,
            InspectedAt = inspectedAt,
            InspectorId = inspectorId,
            IsPass = isPass,
            Remark = remark,
            SampleQuantity = sampleQuantity,
            DefectQuantity = defectQuantity
        };
        result.RestoreAudit(createdBy ?? result.CreatedBy, createdAt ?? result.CreatedAt, updatedBy, updatedAt);
        return result;
    }

    private static string? NormalizeAttributeResult(string? value)
    {
        if (string.Equals(value, "Pass", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "OK", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Good", StringComparison.OrdinalIgnoreCase))
            return "Pass";
        if (string.Equals(value, "Fail", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "NG", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "Bad", StringComparison.OrdinalIgnoreCase))
            return "Fail";
        return null;
    }
}
