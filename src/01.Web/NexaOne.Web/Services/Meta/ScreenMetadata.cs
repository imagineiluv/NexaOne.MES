using System.Text.Json.Serialization;

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
/// <para>
/// <c>Columns</c>가 있으면 그리드를, <c>Fields</c>가 있으면 폼을 렌더하며 둘 다 가능하다.
/// <c>QueryId</c>는 그리드의 데이터 소스 — 파일 기반 쿼리 레지스트리(db/queries)의 명명 쿼리를
/// 가리킨다. 런타임이 <c>/api/v1/query/{QueryId}</c>로 행을 조회해 컬럼 메타에 바인딩한다(고코드
/// 타입드 리포지토리와 공존하는 저코드 조회 경로 — 개발자가 기능별로 선택).
/// <c>SaveQueryId</c>는 폼 저장(쓰기)의 명명 쓰기쿼리(kind="write") — 런타임이 폼 값(필드 Key→값)을
/// <c>/api/v1/command/{SaveQueryId}</c>로 보내 INSERT/UPDATE한다(@param 이름은 필드 Key와 일치).
/// </para>
/// </summary>
public sealed record ScreenDefinition(
    string UiId,
    string Title,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<GridColumnDefinition>? Columns = null,
    string? QueryId = null,
    string? SaveQueryId = null,
    LayoutNode? Layout = null);   // null => 기존 평면 렌더(하위호환). 비null => LayoutRenderer가 렌더.

/// <summary>
/// 레이아웃 트리 노드(Low-Code WYSIWYG). 컨테이너(Section/Row/Column)는 Children을, 위젯은 바인딩을 가진다.
/// discriminator는 "kind"(camelCase 안전). init-only 프로퍼티라 역직렬화가 속성 순서에 의존하지 않는다.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(SectionNode), "section")]
[JsonDerivedType(typeof(RowNode), "row")]
[JsonDerivedType(typeof(ColumnNode), "column")]
[JsonDerivedType(typeof(GridWidget), "grid")]
[JsonDerivedType(typeof(FormWidget), "form")]
[JsonDerivedType(typeof(FieldWidget), "field")]
[JsonDerivedType(typeof(ButtonWidget), "commandButton")]
[JsonDerivedType(typeof(TextWidget), "text")]
public abstract record LayoutNode
{
    /// <summary>GrapesJS 컴포넌트 id == 노드 id(편집 라운드트립 정체성).</summary>
    public string? Id { get; init; }
    /// <summary>UX 힌트 전용 권한(ADR-003 module:action). 서버가 실제 게이트 — 런타임은 표시/비활성만.</summary>
    public string? RequiredPermission { get; init; }
}

// 컨테이너
public sealed record SectionNode : LayoutNode { public string? Title { get; init; } public IReadOnlyList<LayoutNode>? Children { get; init; } }
public sealed record RowNode : LayoutNode { public IReadOnlyList<LayoutNode>? Children { get; init; } }
public sealed record ColumnNode : LayoutNode { public int Span { get; init; } = 12; public IReadOnlyList<LayoutNode>? Children { get; init; } }

// 위젯 — 바인딩을 위젯별로 분리(잘못된 조합을 표현 불가능하게)
public sealed record GridWidget : LayoutNode { public string? QueryId { get; init; } public IReadOnlyList<GridColumnDefinition>? Columns { get; init; } }
public sealed record FormWidget : LayoutNode { public string? SaveQueryId { get; init; } public IReadOnlyList<FieldWidget>? Fields { get; init; } }
public sealed record FieldWidget : LayoutNode { public string? FieldKey { get; init; } public FieldDefinition? Field { get; init; } }
public sealed record ButtonWidget : LayoutNode { public string Label { get; init; } = ""; public string? Command { get; init; } }
public sealed record TextWidget : LayoutNode { public string Text { get; init; } = ""; public bool IsLabel { get; init; } }
