namespace NexaOne.Web.Services.Meta;

/// <summary>메타데이터 화면 필드의 입력 유형(Phase 3 — 단일 화면 런타임).</summary>
public enum FieldType { Text, Number, Date, Boolean, Select }

/// <summary>메타데이터 화면의 단일 입력 필드 정의. 런타임 렌더러가 Type에 따라 컨트롤을 그린다.</summary>
public sealed record FieldDefinition(
    string Key,
    string Label,
    FieldType Type = FieldType.Text,
    bool Required = false,
    bool ReadOnly = false,
    IReadOnlyList<string>? Options = null);

/// <summary>메타데이터 그리드 컬럼 정의.</summary>
public sealed record GridColumnDefinition(string Key, string Caption, bool Visible = true);

/// <summary>
/// 화면 정의(Phase 3). <c>UiId</c>(MenuItem.UiId와 연계 가능)로 식별되며, 런타임 렌더러가 해석해
/// 폼/그리드를 동적으로 그린다 — 손코딩 .razor 없이 메타데이터로 화면을 정의·렌더한다.
/// </summary>
public sealed record ScreenDefinition(
    string UiId,
    string Title,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<GridColumnDefinition>? Columns = null);
