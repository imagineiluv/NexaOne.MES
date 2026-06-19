using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NexaOne.Web.Services.Meta;

/// <summary>ScreenDefinition ↔ JSON 직렬화. DB 저장소(SYS_SCREEN_DEFINITION)·내보내기·디자이너에서 사용.
/// FieldType은 문자열로 직렬화한다. Layout(레이아웃 트리)은 평면 정의와 분리 파싱해, layout이 깨져도
/// (미지 kind·과대 깊이) 화면 전체가 null이 되지 않고 평면 경로로 폴백한다.</summary>
public static class ScreenDefinitionJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    // layout 서브트리 전용 옵션 — 깊이를 명시 제한해 비정상 중첩을 layout 범위 예외로 격리한다.
    private static readonly JsonSerializerOptions LayoutOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        MaxDepth = 32,
    };

    public static string Serialize(ScreenDefinition definition) => JsonSerializer.Serialize(definition, Options);

    public static ScreenDefinition? Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        // 외부 파싱은 깊이를 넉넉히 허용하고(전체 문서가 layout 격리 단계까지 도달하게), 깊이 제한은
        // layout 하위 역직렬화(LayoutOptions.MaxDepth)에서만 강제한다 — 그래야 과대 깊이 layout이
        // 전체 파싱을 죽이지 않고 layout 범위로만 격리돼 평면 정의가 보존된다.
        JsonNode? root;
        try { root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { MaxDepth = 256 }); }
        catch (JsonException) { return null; }
        if (root is not JsonObject obj) return null;

        // 1) layout 서브트리를 떼어 별도 파싱 — 실패하면 layout만 폴백(null), 평면 정의는 보존.
        LayoutNode? layout = null;
        if (obj.TryGetPropertyValue("layout", out var layoutNode) && layoutNode is not null)
        {
            try
            {
                // STJ(.NET 8) 다형 역직렬화는 discriminator("kind")가 객체의 첫 속성이 아니면
                // NotSupportedException을 던진다(읽기 선행 미지원). 외부 입력(DB·디자이너 내보내기)은
                // 속성 순서를 보장하지 않으므로 각 객체의 "kind"를 선두로 재정렬한 뒤 역직렬화한다.
                var normalized = KindFirst(layoutNode);
                // 문자열로 재직렬화 후 LayoutOptions(MaxDepth=32)로 읽기 — 과대 깊이는 읽기 측에서
                // JsonException으로 표면화돼 layout 범위로 격리된다(쓰기 측 깊이 예외 회피).
                layout = JsonSerializer.Deserialize<LayoutNode>(normalized.ToJsonString(), LayoutOptions);
            }
            catch (JsonException) { layout = null; }            // 미지 kind·과대 깊이 등 격리
            catch (NotSupportedException) { layout = null; }    // 다형 생성 불가 등 격리
        }

        // 2) 평면 정의는 layout 키를 제거한 본문으로 복원(전체 역직렬화가 layout 오류로 죽지 않게).
        obj.Remove("layout");
        ScreenDefinition? flat;
        try { flat = obj.Deserialize<ScreenDefinition>(Options); }
        catch (JsonException) { return null; }
        if (flat is null) return null;

        return flat with { Layout = layout };
    }

    /// <summary>모든 객체 노드에서 "kind" 속성을 선두로 재배치(재귀). 다형 discriminator를 첫 속성으로
    /// 만들어 STJ 다형 역직렬화의 first-property 요구를 충족시킨다. 원본은 변형하지 않고 새 트리를 만든다.</summary>
    private static JsonNode? KindFirst(JsonNode? node) => node switch
    {
        JsonObject o => RebuildKindFirst(o),
        JsonArray a => RebuildArray(a),
        _ => node?.DeepClone(),
    };

    private static JsonObject RebuildKindFirst(JsonObject o)
    {
        var rebuilt = new JsonObject();
        if (o.TryGetPropertyValue("kind", out var kind))
            rebuilt["kind"] = KindFirst(kind);
        foreach (var kv in o)
        {
            if (kv.Key == "kind") continue;
            rebuilt[kv.Key] = KindFirst(kv.Value);
        }
        return rebuilt;
    }

    private static JsonArray RebuildArray(JsonArray a)
    {
        var arr = new JsonArray();
        foreach (var item in a) arr.Add(KindFirst(item));
        return arr;
    }
}
