using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexaOne.Web.Services.Meta;

/// <summary>ScreenDefinition ↔ JSON 직렬화(Phase 4 후속). DB 저장소(SYS_SCREEN_DEFINITION)·내보내기·디자이너에서 사용.
/// FieldType은 문자열로 직렬화한다.</summary>
public static class ScreenDefinitionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(ScreenDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static ScreenDefinition? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ScreenDefinition>(json, Options); }
        catch (JsonException) { return null; }
    }
}
