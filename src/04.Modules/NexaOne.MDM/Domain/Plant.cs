using NexaOne.Common;

namespace NexaOne.MDM.Domain;

public sealed class Plant : AuditableEntity<string>
{
    private Plant(string plantId) : base(plantId) { }

    public string PlantName { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public string Country { get; private set; } = string.Empty;
    public string TimeZone { get; private set; } = string.Empty;

    public static Result<Plant> Create(string plantId, string plantName, string country, string timeZone)
    {
        if (string.IsNullOrWhiteSpace(plantId))
            return Result.Failure<Plant>(Error.Validation(nameof(plantId), "Plant ID is required."));
        if (string.IsNullOrWhiteSpace(plantName))
            return Result.Failure<Plant>(Error.Validation(nameof(plantName), "Plant name is required."));

        var plant = new Plant(plantId)
        {
            PlantName = plantName,
            Country = country,
            TimeZone = timeZone
        };
        return plant;
    }

    public void Update(string plantName, string description, string country, string timeZone)
    {
        PlantName = plantName;
        Description = description;
        Country = country;
        TimeZone = timeZone;
    }
}
