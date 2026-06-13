using System.Collections.Concurrent;

namespace NexaOne.Web.Services.Meta;

/// <summary>인메모리 화면 정의 제공자(Phase 3). 시작 시 데모 화면을 시드한다. 싱글톤으로 등록되며
/// 읽기는 동시 안전(ConcurrentDictionary). 향후 MENU/UiId 기반 DB 메타 저장소로 확장 가능.</summary>
public sealed class InMemoryScreenDefinitionProvider : IScreenDefinitionProvider
{
    private readonly ConcurrentDictionary<string, ScreenDefinition> _defs = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryScreenDefinitionProvider()
    {
        // 데모 시드: 메타데이터로 정의한 파라미터 입력 화면(손코딩 .razor 없이 /meta/DEMO_PARAM 으로 렌더)
        Register(new ScreenDefinition("DEMO_PARAM", "데모 — 메타데이터 파라미터", new FieldDefinition[]
        {
            new("parameterId", "파라미터 ID", FieldType.Text, Required: true),
            new("parameterName", "이름", FieldType.Text, Required: true),
            new("unit", "단위", FieldType.Text),
            new("lowerLimit", "하한", FieldType.Number),
            new("upperLimit", "상한", FieldType.Number),
            new("isActive", "활성", FieldType.Boolean),
        }));
    }

    public void Register(ScreenDefinition definition) => _defs[definition.UiId] = definition;

    public bool TryGet(string uiId, out ScreenDefinition? definition)
        => _defs.TryGetValue(uiId ?? string.Empty, out definition);

    public ScreenDefinition? Get(string uiId)
        => _defs.TryGetValue(uiId ?? string.Empty, out var d) ? d : null;
}
