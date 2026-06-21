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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 읽기경로 전용 —
    /// Create는 IsActive=true로 고정하고, 비활성 복원을 위해 Deactivate()를 호출하면 IsDeleted=true·
    /// DeletedAt=UtcNow까지 결합 설정돼 영속 소프트삭제 상태가 손상된다(비삭제 행이 삭제로, 삭제시각이 읽은 시각으로).
    /// Restore는 IsActive·IsDeleted·DeletedAt을 행값 그대로 독립 복원해 이 읽기경로 상태손실을 막는다.</summary>
    public static DefectClass Restore(
        string defectClassId, string defectClassName, string description, string severity,
        bool isActive, bool isDeleted, DateTime? deletedAt,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var defectClass = new DefectClass(defectClassId)
        {
            DefectClassName = defectClassName,
            Description = description,
            Severity = severity,
            IsActive = isActive
        };
        defectClass.RestoreSoftDelete(isDeleted, deletedAt);
        // 감사 메타데이터도 행값 그대로 복원한다 — 미복원 시 매 읽기마다 CreatedAt=UtcNow/CreatedBy="" 리셋(읽기경로 상태손실).
        defectClass.RestoreAudit(createdBy ?? defectClass.CreatedBy, createdAt ?? defectClass.CreatedAt, updatedBy, updatedAt);
        return defectClass;
    }

    public void Deactivate()
    {
        IsActive = false;
        IsDeleted = true;
        DeletedAt = DateTime.UtcNow;
    }
}
