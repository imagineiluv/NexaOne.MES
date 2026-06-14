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
/// </para>
/// </summary>
public sealed record ScreenDefinition(
    string UiId,
    string Title,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<GridColumnDefinition>? Columns = null,
    string? QueryId = null);
