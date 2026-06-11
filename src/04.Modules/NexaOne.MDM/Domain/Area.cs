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

    public void Update(string areaName, string description)
    {
        AreaName = areaName;
        Description = description;
    }
}
