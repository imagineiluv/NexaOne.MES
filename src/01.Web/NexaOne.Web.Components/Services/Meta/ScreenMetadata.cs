using System.Text.Json.Serialization;

namespace NexaOne.Web.Services.Meta;

/// <summary>메타데이터 화면 필드의 입력 유형(Phase 3 — 단일 화면 런타임).</summary>
public enum FieldType { Text, Number, Date, Boolean, Select }

/// <summary>메타데이터 화면의 단일 입력 필드 정의. 런타임 렌더러가 Type에 따라 컨트롤을 그린다.
/// <c>OptionsQueryId</c>는 Select 옵션의 동적 소스(명명 읽기쿼리) — 화면 로드 시 1회 조회해
/// 첫 컬럼=값, 둘째 컬럼=라벨 보조로 옵션을 구성한다(정적 <c>Options</c>가 있으면 그것이 우선).</summary>
public sealed record FieldDefinition(
    string Key,
    string Label,
    FieldType Type = FieldType.Text,
    bool Required = false,
    bool ReadOnly = false,
    IReadOnlyList<string>? Options = null,
    string? OptionsQueryId = null);

/// <summary>Select 옵션의 런타임 표현(값+표시 라벨) — 직렬화 계약이 아닌 렌더러 내부 타입.</summary>
public sealed record MetaFieldOption(string Value, string Label);

/// <summary>메타데이터 그리드 컬럼 정의. Width=고정 폭(px, null=자동) — 표시 순서는 목록 순서가 담당한다(Phase-2).</summary>
public sealed record GridColumnDefinition(string Key, string Caption, bool Visible = true, int? Width = null);

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
    LayoutNode? Layout = null,                 // null => 기존 평면 렌더(하위호환). 비null => LayoutRenderer가 렌더.
    int? RefreshIntervalSeconds = null,        // 자동 새로고침 주기(초, Phase-2 실시간 v2) — null/0=수동(기존 동작).
    IReadOnlyList<FieldDefinition>? SearchFields = null,
    string? CountQueryId = null);
    // SearchFields — 그리드 상단 검색 조건 영역(레거시 SmartUX 표준 패턴). 필드 Key=쿼리 @param 이름이며
    // 조회 시 화면의 모든 읽기쿼리에 함께 바인딩된다(SQL이 선언하지 않은 파라미터는 게이트웨이가 무시,
    // 누락 파라미터는 DBNull → NULL-가드 쿼리에서 전체 조회). §20.8 조건 저장/불러오기의 대상이기도 하다.
    // CountQueryId — 서버측 페이징(P3-9 v2). 지정 시 QueryId 조회에 @limit/@offset 파라미터를 함께 바인딩하고,
    // 같은 검색 조건으로 총건수(단일 행 첫 컬럼)를 조회해 서버 페이저를 렌더한다. null=클라이언트 페이징(현행).
    // 대상 SQL은 방언별 페이징 절(@limit/@offset)을 선언해야 하며, 짝 count 쿼리와 WHERE를 일치시킨다.

/// <summary>
/// 레이아웃 트리 노드(Low-Code WYSIWYG). 컨테이너(Section/Row/Column)는 Children을, 위젯은 바인딩을 가진다.
/// discriminator는 "kind"(camelCase 안전). .NET 8 STJ 다형 역직렬화는 "kind"가 객체의 첫 속성이어야 하므로,
/// 외부 생산 JSON의 키 순서는 ScreenDefinitionJson이 로드 시 정규화(KindFirst)한다.
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
[JsonDerivedType(typeof(KpiWidget), "kpi")]
[JsonDerivedType(typeof(BadgeWidget), "statusBadge")]
[JsonDerivedType(typeof(TrendChartWidget), "trendChart")]
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
/// <summary>폼 위젯. Isolated=true(Phase-2 멀티폼)면 화면 공유 Model 대신 폼 전용 모델에 바인딩된다 —
/// 한 화면에 독립 폼 여러 개(각자 저장/검증)가 가능해진다. 기본 false = 기존 공유 모델(하위호환).
/// 격리 키는 Id(우선) 또는 SaveQueryId — 둘 다 없으면 격리 불가(공유로 저하).</summary>
public sealed record FormWidget : LayoutNode
{
    public string? SaveQueryId { get; init; }
    public IReadOnlyList<FieldWidget>? Fields { get; init; }
    public bool Isolated { get; init; }
}
public sealed record FieldWidget : LayoutNode { public string? FieldKey { get; init; } public FieldDefinition? Field { get; init; } }
/// <summary>명령 버튼. <c>ConfirmMessage</c>가 있으면 실행 전 브라우저 확인을 통과해야 한다(파괴적 명령 보호, P1-2).</summary>
public sealed record ButtonWidget : LayoutNode { public string Label { get; init; } = ""; public string? Command { get; init; } public string? ConfirmMessage { get; init; } }
public sealed record TextWidget : LayoutNode { public string Text { get; init; } = ""; public bool IsLabel { get; init; } }
/// <summary>KPI 카드(디자이너 Phase-2) — QueryId 결과 첫 행의 ValueColumn 값을 큰 숫자로 표시. 대시보드 요약용.</summary>
public sealed record KpiWidget : LayoutNode
{
    public string Label { get; init; } = "";
    public string? QueryId { get; init; }
    public string? ValueColumn { get; init; }
    public string? Unit { get; init; }
    /// <summary>드릴다운 대상 화면 UiId(P3-12) — 지정 시 KPI 카드 클릭이 /meta/{LinkUiId}로 이동한다.</summary>
    public string? LinkUiId { get; init; }
}
/// <summary>상태 뱃지(디자이너 Phase-2) — QueryId 결과 첫 행의 ValueColumn 값을 스타일 규칙(값→심각도)에
/// 매칭해 색상 뱃지로 표시한다. 규칙 미매칭 값은 neutral로 원문 표시(상태 추가가 화면을 깨지 않게).</summary>
public sealed record BadgeWidget : LayoutNode
{
    public string? Label { get; init; }
    public string? QueryId { get; init; }
    public string? ValueColumn { get; init; }
    public IReadOnlyList<BadgeStyleRule>? Styles { get; init; }
}
/// <summary>뱃지 스타일 규칙 — Value(대소문자 무시 매칭) → Severity(success|warning|danger|info|neutral).
/// DisplayText가 있으면 원문 대신 표시(예: "RUN"→"가동").</summary>
public sealed record BadgeStyleRule(string Value, string Severity, string? DisplayText = null);
/// <summary>트렌드 차트(Phase-2 실시간 v2) — 바인딩 쿼리 행의 ValueColumn 수치를 네이티브 SVG 라인으로
/// 그린다(외부 차트 라이브러리 없음). 마지막 MaxPoints개만 표시 — RefreshIntervalSeconds와 조합하면
/// 준실시간 스트리밍 차트가 된다(SignalR 푸시 정밀화는 후속).</summary>
public sealed record TrendChartWidget : LayoutNode
{
    public string Label { get; init; } = "";
    public string? QueryId { get; init; }
    public string? ValueColumn { get; init; }
    public int MaxPoints { get; init; } = 50;
    /// <summary>시간축 컬럼(P3-13) — 지정 시 첫/마지막 표본 시각을 라벨로, 포인트 툴팁에 시각을 함께 표시한다.</summary>
    public string? TimeColumn { get; init; }
}
