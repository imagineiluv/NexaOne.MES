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

    /// <summary>영속 데이터로부터 전체 상태를 복원한다(검증 없이 신뢰). 리포지토리 읽기 전용 —
    /// Create는 Description을 받지 않아 읽기경로에서 매번 공백으로 초기화되는 상태손실을 막는다.</summary>
    public static Plant Restore(
        string plantId, string plantName, string description, string country, string timeZone)
        => new(plantId)
        {
            PlantName = plantName,
            Description = description,
            Country = country,
            TimeZone = timeZone
        };

    public void Update(string plantName, string description, string country, string timeZone)
    {
        PlantName = plantName;
        Description = description;
        Country = country;
        TimeZone = timeZone;
    }
}
