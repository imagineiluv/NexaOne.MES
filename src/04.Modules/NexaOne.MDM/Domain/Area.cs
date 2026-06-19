using NexaOne.Common;

namespace NexaOne.MDM.Domain;

public sealed class Area : AuditableEntity<string>
{
    private Area(string areaId) : base(areaId) { }

    public string AreaName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string PlantId { get; private set; } = string.Empty;

    public static Result<Area> Create(string areaId, string areaName, string plantId)
    {
        if (string.IsNullOrWhiteSpace(areaId))
            return Result.Failure<Area>(Error.Validation(nameof(areaId), "Area ID is required."));
        if (string.IsNullOrWhiteSpace(areaName))
            return Result.Failure<Area>(Error.Validation(nameof(areaName), "Area name is required."));

        var area = new Area(areaId)
        {
            AreaName = areaName,
            PlantId = plantId
        };
        return area;
    }

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 Description을 받지 않아, 저장된 설명이 읽기경로에서 빈 문자열로 유실되는 상태손실을 막는다.</summary>
    public static Area Restore(string areaId, string areaName, string description, string plantId,
        string? createdBy = null, DateTime? createdAt = null, string? updatedBy = null, DateTime? updatedAt = null)
    {
        var area = new Area(areaId)
        {
            AreaName = areaName,
            Description = description,
            PlantId = plantId
        };
        area.RestoreAudit(createdBy ?? area.CreatedBy, createdAt ?? area.CreatedAt, updatedBy, updatedAt);
        return area;
    }

    public void Update(string areaName, string description)
    {
        AreaName = areaName;
        Description = description;
    }
}
