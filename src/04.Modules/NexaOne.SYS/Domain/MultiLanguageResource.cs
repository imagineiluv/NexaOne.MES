using NexaOne.Common;

namespace NexaOne.SYS.Domain;

public sealed class MultiLanguageResource : Entity<string>
{
    private MultiLanguageResource(string resourceKey) : base(resourceKey) { }

    public string MenuId { get; private set; } = string.Empty;
    public LanguageType Language { get; private set; }
    public string Value { get; private set; } = string.Empty;

    public static MultiLanguageResource Create(
        string resourceKey,
        string menuId,
        LanguageType language,
        string value) =>
        new(resourceKey) { MenuId = menuId, Language = language, Value = value };

    public void UpdateValue(string value) => Value = value;
}
