using NexaOne.Common;

namespace NexaOne.MDM.Domain;

public sealed class Equipment : AuditableEntity<string>
{
    private Equipment(string equipmentId) : base(equipmentId) { }

    public string EquipmentName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;
    public string AreaId { get; private set; } = string.Empty;
    public string EquipmentType { get; private set; } = string.Empty;
    public string? ParentEquipmentId { get; private set; }
    public string Vendor { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string ValidState { get; private set; } = "Valid";
    public string EquipmentClassId { get; private set; } = string.Empty;

    public static Result<Equipment> Create(
        string equipmentId,
        string equipmentName,
        string plantId,
        string areaId,
        string equipmentType,
        string? parentEquipmentId = null,
        string vendor = "",
        string model = "",
        string equipmentClassId = "")
    {
        if (string.IsNullOrWhiteSpace(equipmentId))
            return Result.Failure<Equipment>(Error.Validation(nameof(equipmentId), "Equipment ID is required."));
        if (string.IsNullOrWhiteSpace(equipmentName))
            return Result.Failure<Equipment>(Error.Validation(nameof(equipmentName), "Equipment name is required."));

        var equipment = new Equipment(equipmentId)
        {
            EquipmentName = equipmentName,
            PlantId = plantId,
            AreaId = areaId,
            EquipmentType = equipmentType,
            ParentEquipmentId = parentEquipmentId,
            Vendor = vendor,
            Model = model,
            EquipmentClassId = equipmentClassId,
            ValidState = "Valid"
        };
        return equipment;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 ValidState를 항상 "Valid"로 하드코딩하고 Description 인자가 없어, 비활성화된(Invalid) 설비가
    /// 읽기경로에서 다시 "Valid"로 되살아나고 Description이 유실되는 상태손실을 막는다.</summary>
    public static Equipment Restore(
        string equipmentId, string equipmentName, string description, string plantId, string areaId,
        string equipmentType, string? parentEquipmentId, string vendor, string model,
        string equipmentClassId, string validState,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var equipment = new Equipment(equipmentId)
        {
            EquipmentName = equipmentName,
            Description = description,
            PlantId = plantId,
            AreaId = areaId,
            EquipmentType = equipmentType,
            ParentEquipmentId = parentEquipmentId,
            Vendor = vendor,
            Model = model,
            EquipmentClassId = equipmentClassId,
            ValidState = validState
        };
        // 읽기경로 Restore 패턴: 영속된 감사 메타데이터를 그대로 복원(미복원 시 CreatedAt이 매 읽기마다 UtcNow로
        // 재생성되고 CreatedBy=""·UpdatedBy/At=null로 리셋되는 상태손실). 선택적 파라미터라 기존 호출부는 그대로 컴파일된다.
        equipment.RestoreAudit(createdBy ?? equipment.CreatedBy, createdAt ?? equipment.CreatedAt, updatedBy, updatedAt);
        return equipment;
    }

    public void Deactivate()
    {
        ValidState = "Invalid";
        // ADR-002: 비활성화를 도메인 이벤트로 발행한다. 리포가 비활성화(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        // (Restore는 new(...) 직접 경로라 이벤트를 발행하지 않는다 — 읽기경로 재구성은 발행 대상이 아니다.)
        RaiseDomainEvent(new EquipmentDeactivatedDomainEvent(Id));
    }

    public void Activate()
    {
        ValidState = "Valid";
        // ADR-002: 활성화를 도메인 이벤트로 발행한다. 리포가 활성화(UPDATE)와 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new EquipmentActivatedDomainEvent(Id));
    }

    public void ChangeParent(string? parentId)
    {
        ParentEquipmentId = parentId;
        // ADR-002: 상위 변경을 도메인 이벤트로 발행한다. 리포가 상위 변경(UPDATE)과 동일 트랜잭션에 outbox로 기록한다(opt-in).
        RaiseDomainEvent(new EquipmentParentChangedDomainEvent(Id, parentId));
    }

    public void UpdateInfo(string name, string description, string equipmentType, string vendor, string model)
    {
        EquipmentName = name;
        Description = description;
        EquipmentType = equipmentType;
        Vendor = vendor;
        Model = model;
    }
}
