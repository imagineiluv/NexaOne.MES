using NexaOne.Common;

namespace NexaOne.QMS.Domain;

public sealed class Defect : AuditableEntity<string>
{
    private Defect(string defectId) : base(defectId) { }

    public string LotId { get; private set; } = string.Empty;
    public string EquipmentId { get; private set; } = string.Empty;
    public string DefectClassId { get; private set; } = string.Empty;
    public int DefectCount { get; private set; }
    public decimal DefectRate { get; private set; }
    public DateTime InspectedAt { get; private set; }
    public string InspectorId { get; private set; } = string.Empty;
    public DateTime? ConfirmedAt { get; private set; }
    public string? Remark { get; private set; }
    public bool IsConfirmed { get; private set; }

    public static Result<Defect> Create(
        string defectId,
        string lotId,
        string equipmentId,
        string defectClassId,
        int defectCount,
        decimal defectRate,
        DateTime inspectedAt,
        string inspectorId,
        string? remark = null)
    {
        if (string.IsNullOrWhiteSpace(defectId))
            return Result.Failure<Defect>(Error.Validation(nameof(defectId), "Defect ID is required."));
        if (string.IsNullOrWhiteSpace(lotId))
            return Result.Failure<Defect>(Error.Validation(nameof(lotId), "Lot ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<Defect>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(defectClassId))
            return Result.Failure<Defect>(Error.Validation(nameof(defectClassId), "Defect class ID is required."));
        if (defectCount < 0)
            return Result.Failure<Defect>(Error.Validation(nameof(defectCount), "Defect count must be non-negative."));
        if (defectRate < 0)
            return Result.Failure<Defect>(Error.Validation(nameof(defectRate), "Defect rate must be non-negative."));
        if (string.IsNullOrWhiteSpace(inspectorId))
            return Result.Failure<Defect>(Error.Validation(nameof(inspectorId), "Inspector ID is required."));

        var defect = new Defect(defectId)
        {
            LotId = lotId,
            EquipmentId = equipmentId,
            DefectClassId = defectClassId,
            DefectCount = defectCount,
            DefectRate = defectRate,
            InspectedAt = inspectedAt,
            InspectorId = inspectorId,
            Remark = remark,
            IsConfirmed = false
        };
        return defect;
    }

    public Result Confirm(string confirmerId)
    {
        if (string.IsNullOrWhiteSpace(confirmerId))
            return Result.Failure(Error.Validation(nameof(confirmerId), "Confirmer ID is required."));
        if (IsConfirmed)
            return Result.Failure(Error.Conflict("Defect is already confirmed."));

        IsConfirmed = true;
        ConfirmedAt = DateTime.UtcNow;
        UpdatedBy = confirmerId;
        UpdatedAt = DateTime.UtcNow;
        return Result.Success();
    }
}
