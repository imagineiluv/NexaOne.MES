using NexaOne.Common;

namespace NexaOne.QMS.Domain;

public sealed class DefectClass : AuditableEntity<string>
{
    private static readonly HashSet<string> ValidSeverities = ["Critical", "Major", "Minor"];

    private DefectClass(string defectClassId) : base(defectClassId) { }

    public string DefectClassName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Severity { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    public static Result<DefectClass> Create(
        string defectClassId,
        string defectClassName,
        string description,
        string severity)
    {
        if (string.IsNullOrWhiteSpace(defectClassId))
            return Result.Failure<DefectClass>(Error.Validation(nameof(defectClassId), "Defect class ID is required."));
        if (string.IsNullOrWhiteSpace(defectClassName))
            return Result.Failure<DefectClass>(Error.Validation(nameof(defectClassName), "Defect class name is required."));
        if (!ValidSeverities.Contains(severity))
            return Result.Failure<DefectClass>(Error.Validation(nameof(severity), "Severity must be 'Critical', 'Major', or 'Minor'."));

        var defectClass = new DefectClass(defectClassId)
        {
            DefectClassName = defectClassName,
            Description = description,
            Severity = severity,
            IsActive = true
        };
        return defectClass;
    }

    public void Deactivate()
    {
        IsActive = false;
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
    }
}
