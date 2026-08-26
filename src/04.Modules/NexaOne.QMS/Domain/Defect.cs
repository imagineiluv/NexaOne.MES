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
        if (defectRate < 0 || defectRate > 1)
            return Result.Failure<Defect>(Error.Validation(nameof(defectRate), "Defect rate must be between 0 and 1."));
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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 항상 IsConfirmed=false로 강제하므로 확정된 부적합이 미확정으로 잘못 읽히고(읽기경로 상태손실)
    /// 재확정 방지 가드가 무력화되는 것을 막는다. 확정자(UpdatedBy)는 비-부인성 감사값이라 함께 복원한다.</summary>
    public static Defect Restore(
        string defectId, string lotId, string equipmentId, string defectClassId,
        int defectCount, decimal defectRate, DateTime inspectedAt, string inspectorId,
        string? remark, bool isConfirmed, DateTime? confirmedAt, string? updatedBy, DateTime? updatedAt,
        string? createdBy = null, DateTime? createdAt = null)
    {
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
            IsConfirmed = isConfirmed,
            ConfirmedAt = confirmedAt
        };
        // 감사 메타데이터를 일원화해 복원한다(이중 세팅 방지). updatedBy/updatedAt는 기존 비즈니스 파라미터를 그대로 전달.
        defect.RestoreAudit(createdBy ?? defect.CreatedBy, createdAt ?? defect.CreatedAt, updatedBy, updatedAt);
        return defect;
    }

    public Result Confirm(string confirmerId)
    {
        if (string.IsNullOrWhiteSpace(confirmerId))
            return Result.Failure(Error.Validation(nameof(confirmerId), "Confirmer ID is required."));
        if (IsConfirmed)
            return Result.Failure(Error.Conflict("Defect is already confirmed."));

        var confirmedAt = DateTime.UtcNow;
        IsConfirmed = true;
        ConfirmedAt = confirmedAt;
        UpdatedBy = confirmerId;
        UpdatedAt = confirmedAt;
        // ADR-002: 부적합 확정을 도메인 이벤트로 발행한다. 리포가 확정(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        RaiseDomainEvent(new DefectConfirmedDomainEvent(
            Id, LotId, DefectClassId, EquipmentId, DefectCount, DefectRate, confirmerId, confirmedAt));
        return Result.Success();
    }
}
