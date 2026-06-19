# GrapesJS 화면 디자이너 설계 (Low-Code WYSIWYG)

> 상태: 승인 대기(브레인스토밍 산출 스펙) · 작성일 2026-06-19
> 관련: [Frontend-Coexistence.md](../Frontend-Coexistence.md) (상위 공존 아키텍처), ADR-002(이벤트 버스/아웃박스), ADR-003(보안 PEP)

## 1. 목적과 범위

현업이 위젯을 캔버스에 드래그&드롭으로 배치해 MES 화면을 시각적으로 조립하고, 만들어진 화면이 기존 플랫폼(Runtime / Query Engine / Security / Event Bus)을 그대로 통과하게 한다. 현재 `/designer`는 정의-입력 폼 + 라이브 프리뷰이고 `ScreenDefinition` 모델에는 좌표·레이아웃 개념이 없다. 이 설계는 **GrapesJS 기반 진짜 WYSIWYG 디자이너**와 그 산출물을 렌더하는 **레이아웃 인지 런타임**을 추가하되, 기존 정의-주도 런타임과 완전 하위호환을 유지한다.

이 설계는 자유형 HTML 페이지 빌더가 아니라 **플랫폼 연동형 Low-Code MES 화면 빌더**다. GrapesJS는 시각 에디터로만 쓰고, 끌어다 놓는 컴포넌트는 플랫폼 프리미티브(명명 쿼리에 바인딩된 데이터 그리드, 저장 쿼리에 바인딩된 폼/필드, 명령 버튼 등)이며, 산출물은 GrapesJS 사설 HTML이 아니라 **렌더러 중립적 레이아웃 트리**다.

## 2. 확정된 결정 (브레인스토밍)

| 갈림길 | 결정 | 근거 |
|---|---|---|
| 디자이너 호스팅 | **React SPA (NexaOne.Spa)** | GrapesJS가 바닐라 JS라 React 생태계에 자연스러움 |
| 런타임 렌더러 | **Blazor `/meta`** | 기존 `MetaGridRenderer`/`MetaFormRenderer`/명명 쿼리 경로 재사용; 디자인된 화면이 기존 40여 화면·메뉴·MDI 셸 안에 그대로 위치 |
| 저장 산출물 형식 | **접근 A — 구조화된 레이아웃 스키마** | Blazor 런타임이 HTML 파싱 없이 깨끗; 산출물이 플랫폼 중립·재편집 가능; XSS 표면 없음 |
| 직렬화 권위 | **C# `ScreenDefinition` 계약이 유일 진리원천** | SPA는 NSwag 생성 TS 형태를 타깃(병렬 손작성 직렬화기 금지) |
| Phase 1 멀티 쿼리 | **멀티 read 지원** | 여러 그리드/위젯을 한 화면에 조합하는 것이 WYSIWYG의 실질 가치 |
| Phase 1 컴포넌트 세트 | **트림** (Section/Row/Column + DataGrid + Form/Field + Text + CommandButton) | KPI·StatusBadge는 배지-스타일 모델 확정 후 Phase 2 |

## 3. 검증된 기존 계약 (실제 코드 기준)

설계는 다음 사실 위에 선다(2026-06-19 코드 정밀 분석 + .NET 8 STJ 실증).

- **모델**: `sealed record ScreenDefinition(string UiId, string Title, IReadOnlyList<FieldDefinition> Fields, IReadOnlyList<GridColumnDefinition>? Columns = null, string? QueryId = null, string? SaveQueryId = null)` — [ScreenMetadata.cs:30](../../../src/01.Web/NexaOne.Web/Services/Meta/ScreenMetadata.cs). `Fields`는 비널 필수, 나머지는 선택. 모두 불변 record.
- **직렬화**: `ScreenDefinitionJson` — `JsonSerializerDefaults.Web`(camelCase) + `JsonStringEnumConverter` + `WriteIndented`. null 필드도 출력. **`Deserialize`는 `JsonException`을 잡아 null 반환** — 이것이 후술 위험의 증폭기다.
- **런타임**: `MetaScreen.razor`(`@page "/meta/{UiId}"`)가 provider 캐시→API GET→`Deserialize`→register. 현재 **read 쿼리 최대 1개**(`QueryId`)를 `ExecuteQueryAsync`로 실행해 `MetaGridRenderer`에 공급. 저장은 `ExecuteCommandAsync(SaveQueryId, _model)`. `Validate()`는 **평면 `Fields`만 순회**.
- **저장**: `PUT /api/v1/sys/screen-definitions/{uiId}`(`[Authorize(Policy="perm:sys:manage")]`) → `SYS_SCREEN_DEFINITION.DEFINITION_JSON`(NVARCHAR(MAX), **불투명**). 백엔드는 구조를 파싱/검증하지 않음(프론트가 구조 소유). **kind 컬럼·discriminator 컬럼 없음**.
- **쿼리 엔진**: `POST /api/v1/query/{id}`(read, 결과 = `IReadOnlyList<Dictionary<string,object?>>`), `POST /api/v1/command/{id}`(write, `AffectedRowsResponse{Affected}`). write 쿼리는 `requiredPermission` 필수(부팅 시 fail-fast). `@currentUser/@utcNow`는 **서버 주입**(클라이언트 위조 불가). SQL에 없는 파라미터는 무시.
- **쿼리 레지스트리**: `IQueryRegistry.Ids`는 **문자열만** 반환 — `IsWrite`/`RequiredPermission` 없음, 노출 HTTP 엔드포인트 없음.
- **SPA**: Vite base `/spa/`, 빌드 출력 `wwwroot/spa`, dev 프록시 5173→5181, NSwag 타입 클라이언트(`screenDefinitionsAll/GET/PUT`). 라우터 라이브러리 없음(상태 기반). **GrapesJS·ScreenEditor 부재**. 인증은 `apiFetch` 래퍼가 Bearer 부착(생성 Client 자체는 부착 안 함).
- **보안 경계**: 실제 게이트는 서버 — 등록된 쿼리만 실행, write는 `requiredPermission` 강제, 임의 SQL은 admin 전용. Blazor 렌더러는 Razor `@`-보간(자동 인코딩), `MarkupString`/`Html.Raw` 없음.

## 4. 적대적 검증이 강제한 핵심 수정

초안 스키마를 실제 .NET 8 STJ에 돌려 재현한 결함과 수정. **이 수정들이 설계의 뼈대다.**

1. **위치 의존 역직렬화 (HIGH).** STJ 다형성을 *위치 기반 record*에 쓰면 `"kind"`가 객체의 첫 속성이 아닐 때 `NotSupportedException`. SPA(또는 JSON 포매터)는 키 순서를 보장 못 함.
   → **수정: 모든 레이아웃 노드를 init-only 프로퍼티 record(매개변수 없는 생성자)로** 정의 → discriminator 위치 무관하게 역직렬화. "kind 첫 속성" 가정 폐기.
2. **파싱 실패가 화면 전체를 백지화 (HIGH).** 알 수 없는 kind나 MaxDepth(기본 64) 초과 → `JsonException` → `Deserialize`가 null로 삼킴 → 화면 전체 "정의 없음". 미래 디자이너 버전·오타 하나로 발생.
   → **수정: 2단계 분리 파싱** — 평면 정의(Fields/Columns)를 먼저 복원, layout은 별도 단계로 파싱. layout이 깨지면 **평면 경로로 폴백**(전체 null 아님), 예외는 삼키지 말고 로깅. MaxDepth 명시·클램프.
3. **재사용 대상 부재 (HIGH).** SPA에 에디터·GrapesJS 없음; `IQueryRegistry.Ids`는 문자열만, 노출 엔드포인트 없음.
   → **수정: 쿼리 메타데이터 엔드포인트를 선행 과제로 명시**(§9). C# 계약을 단일 권위로, TS 형태는 생성/검증(병렬 손작성 금지).
4. **장식적 권한 (MEDIUM).** layout의 `RequiredPermission`은 UX 전용 — 서버 게이트(쿼리의 실제 requiredPermission)를 좁힐 수 없고 불일치 가능.
   → **수정: UX 힌트임을 명문화**, 비활성 상태는 별도 필드가 아니라 **쿼리의 실제 requiredPermission**(§9 엔드포인트)에서 유도, taxonomy 검증.
5. **검증 진리원천 중복 (MEDIUM, 사용자의 알려진 버그류).** 초안의 "FieldWidget을 평면 Fields에 미러링"은 읽기경로 Restore 중복 버그류.
   → **수정: `Layout != null`이면 `Validate()`가 레이아웃 트리를 걸어 FieldWidget 집합으로 검증**(단일 출처).
6. **멀티쿼리 미구현 (MEDIUM).** 현재 MetaScreen은 read 1개. 멀티 read 지원으로 확정했으므로 **per-위젯 결과맵 오케스트레이터를 명시적 스코프**로 포함(§6).

## 5. 레이아웃 스키마

`ScreenDefinition`에 **맨 끝 선택적 매개변수 `Layout`(기본 null)**을 추가한다. 노드는 모두 **init-only record**(위치 독립 역직렬화).

```csharp
public sealed record ScreenDefinition(
    string UiId, string Title,
    IReadOnlyList<FieldDefinition> Fields,
    IReadOnlyList<GridColumnDefinition>? Columns = null,
    string? QueryId = null, string? SaveQueryId = null,
    LayoutNode? Layout = null);   // null => 기존 평면 렌더

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
    public string? Id { get; init; }                 // GrapesJS 컴포넌트 id == 노드 id (라운드트립 정체성)
    public string? RequiredPermission { get; init; } // UX 힌트 전용 — §7
}

// 컨테이너
public sealed record SectionNode : LayoutNode { public string? Title { get; init; } public IReadOnlyList<LayoutNode>? Children { get; init; } }
public sealed record RowNode     : LayoutNode { public IReadOnlyList<LayoutNode>? Children { get; init; } }
public sealed record ColumnNode  : LayoutNode { public int Span { get; init; } = 12; public IReadOnlyList<LayoutNode>? Children { get; init; } }

// 위젯 — 바인딩을 위젯별로 분리(잘못된 조합을 표현 불가능하게)
public sealed record GridWidget   : LayoutNode { public string? QueryId { get; init; } public IReadOnlyList<GridColumnDefinition>? Columns { get; init; } }
public sealed record FormWidget   : LayoutNode { public string? SaveQueryId { get; init; } public IReadOnlyList<FieldWidget>? Fields { get; init; } }
public sealed record FieldWidget  : LayoutNode { public string? FieldKey { get; init; } public FieldDefinition? Field { get; init; } }
public sealed record ButtonWidget : LayoutNode { public string Label { get; init; } = ""; public string? Command { get; init; } }
public sealed record TextWidget   : LayoutNode { public string Text { get; init; } = ""; public bool IsLabel { get; init; } }
```

- 직렬화는 기존 `ScreenDefinitionJson` 옵션(camelCase + `JsonStringEnumConverter`)을 그대로 사용. `kind`는 camelCase 안전한 별도 discriminator로 `FieldType` enum 변환과 충돌 없음.
- 컨테이너는 `Children`, 위젯은 leaf. GrapesJS 컴포넌트 트리와 1:1 매핑.
- `GridColumnDefinition`은 기존 그대로 재사용(폭/순서 메타데이터는 Phase 1 비대상 — 현재도 없음).

### JSON 예시 (그리드 + 폼 + 저장 버튼)

```json
{
  "uiId": "PLANT_MGMT", "title": "공장 관리",
  "fields": [ { "key": "plantId", "label": "공장 ID", "type": "Text", "required": true, "readOnly": false, "options": null } ],
  "columns": null, "queryId": null, "saveQueryId": null,
  "layout": {
    "kind": "section", "id": "sec-root", "title": "공장 마스터",
    "children": [ { "kind": "row", "id": "row-1", "children": [
      { "kind": "column", "span": 7, "children": [
        { "kind": "grid", "id": "grid-plants", "queryId": "MDM.PlantList",
          "columns": [ { "key": "PLANT_ID", "caption": "공장 ID", "visible": true } ] } ] },
      { "kind": "column", "span": 5, "children": [
        { "kind": "form", "id": "form-plant", "saveQueryId": "MDM.CreatePlant",
          "fields": [ { "kind": "field", "fieldKey": "plantId",
            "field": { "key": "plantId", "label": "공장 ID", "type": "Text", "required": true, "readOnly": false, "options": null } } ] },
        { "kind": "commandButton", "label": "저장", "requiredPermission": "mdm:manage", "command": "MDM.CreatePlant" } ] }
    ] } ]
  }
}
```

## 6. 런타임 렌더러 (Blazor `/meta`)

`MetaScreen`이 단일 오케스트레이터 역할을 유지(load/validate/save 소유, 렌더러는 dumb)하고, 신규 **재귀 `LayoutRenderer`**가 트리를 렌더한다.

- **분기**: `if (_definition.Layout is null) { 기존 평면 경로 } else { <LayoutRenderer Layout=... Model=... QueryResults=... /> }`. 평면 경로는 무변경.
- **멀티 read 오케스트레이션 (확정 스코프)**: `MetaScreen`이 로드 후 레이아웃 트리를 1회 걸어 **서로 다른 `GridWidget.QueryId`를 수집**하고, 각 queryId를 1회 `ExecuteQueryAsync` 실행해 `Dictionary<queryId, IReadOnlyList<Dictionary<string,object?>>>` 결과맵을 만든 뒤 `LayoutRenderer`에 내려보낸다. 각 `GridWidget`는 자기 queryId의 결과로 기존 `MetaGridRenderer`(시그니처 무변경)를 렌더. 위젯별 로딩/빈/오류 상태는 결과맵 항목으로 표현.
- **폼/필드**: `FormWidget`의 `Fields`로 인메모리 `ScreenDefinition`을 합성해 기존 `MetaFormRenderer`(무변경)에 위임, **공유 Model dict + ModelChanged**를 재귀로 전달(Blazor cascading + EventCallback).
- **저장/명령**: `CommandButton` 클릭 → `MetaScreen`이 `ExecuteCommandAsync(command, _model)`. 화면 공유 Model dict 하나를 사용(Phase 1은 다중 독립 폼 모델 비지원).
- **검증 단일 출처**: `Layout != null`이면 `Validate()`가 트리를 걸어 `FieldWidget`(필수/읽기전용)을 검증 — 평면 Fields 미러링 금지.
- **안전성**: layout 파싱은 평면 정의와 분리(§4-2). `LayoutRenderer`는 Razor `@`-보간만, `MarkupString`/`Html.Raw` 금지(§7).

## 7. 보안 · 감사 · 이벤트

진짜 경계는 서버다(검증 확인).

- **권한**: CommandButton은 등록된 write 쿼리(kind=write + `requiredPermission`)만 호출 가능. 임의 SQL은 admin 전용. 저장(PUT)은 `perm:sys:manage`.
- **layout `RequiredPermission`은 UX 힌트 전용** — 서버 게이트를 좁히지 못한다. UX 비활성 상태는 별도 필드가 아니라 **쿼리의 실제 `requiredPermission`**(§9 엔드포인트)에서 유도하고, 디자인 시 taxonomy(`module:action`)로 검증.
- **감사**: `@currentUser/@utcNow`는 서버 주입·클라이언트 값 덮어씀(위조 불가). SQL에 없는 Model 키 무시(파라미터 주입 없음). queryId/command는 `Uri.EscapeDataString` + 미등록 시 404(경로/SQL 주입 없음).
- **XSS (명시 제약)**: 신규 `LayoutRenderer`/위젯은 Razor `@`-보간만 사용, `MarkupString`/`Html.Raw` 금지 → Text/캡션 자동 인코딩. GrapesJS 캔버스 RTE/raw HTML 트레이트 비노출(§8)이라 저장 트리에 마크업 자체가 들어오지 않음.
- **이벤트(ADR-002)**: CommandButton이 도메인 이벤트를 일으키는 write 쿼리를 호출하면, 기존 아웃박스 트랜잭션(opt-in `Events:Outbox:Enabled`)이 그대로 적용된다. 디자이너/런타임은 추가 작업 없음.

## 8. 디자이너 (React SPA + GrapesJS)

- **호스팅**: `src/01.Web/NexaOne.Spa`에 `npm i grapesjs`, 신규 `features/ScreenEditor.tsx` 라우트. Vite base `/spa/` 유지, 빌드 산출물 `wwwroot/spa`. 인증은 기존 `apiFetch`/JWT 흐름.
- **잠금(lockdown)**: GrapesJS 기본 블록/RTE 비활성. §5의 8개 컴포넌트만 블록 팔레트로 등록. 컨테이너 중첩 규칙(Section⊃Row/위젯, Row⊃Column, Column⊃임의). 트레이트는 바인딩/설정 키만 노출(raw HTML/CSS/style 트레이트 없음) → 직렬화 트리에 GrapesJS 사설 스타일이 새지 않음.
- **저장/로드 매핑**: 커스텀 StorageManager가 GrapesJS 컴포넌트 트리 ↔ `LayoutNode`를 기계적으로 매핑. 저장 시 `LayoutNode`(+ 하위호환용 평면 필드)를 §3 직렬화 형태로 만들어 `screenDefinitionsPUT`. 로드 시 `layout` 있으면 트리를 GrapesJS 컴포넌트로 복원(`id` 라운드트립), 없으면 평면 Fields/Columns에서 기본 레이아웃 자동 생성(레거시 화면을 편집 가능하게).
- **단일 진리원천**: SPA는 C# 계약에서 생성된 TS 타입 형태를 타깃(병렬 손작성 직렬화기 금지). 노드 생성 시 항상 `Id` 부여.
- **드롭다운**: queryId/command 트레이트는 §9 메타데이터 엔드포인트로 채운 드롭다운(read/write 필터)으로 제공 — 오타·404 방지, UX 권한 비활성의 출처.

## 9. 신규 백엔드 의존성 (선행)

`GET /api/v1/sys/queries` → `[{ id, isWrite, requiredPermission }]`. `IQueryRegistry`를 확장해(현재 `Ids`만 노출) `QueryDefinition`의 `IsWrite`/`RequiredPermission`을 함께 노출. 디자이너 드롭다운과 UX 권한 비활성의 단일 출처. `[Authorize(Policy="perm:sys:manage")]`(디자이너 사용자 = 화면 관리자).

## 10. 하위호환

- `Layout`은 맨 끝 선택적 매개변수(기본 null) → 모든 기존 호출부 컴파일(ScreenDesigner.BuildDefinition, 3개 InMemory 시드). 옛 JSON엔 `layout` 키 없음 → null 복원. MetaScreen 평면 분기 무변경.
- 백엔드 무변경(definitionJson 불투명, 새 컬럼/discriminator 컬럼 없음 — kind는 JSON 내부, 기존 계약 일치).
- 기존 Blazor `ScreenDesigner.razor`(정의-폼)는 유지. Phase 1은 GrapesJS 디자이너를 **병행 추가**(대체 아님). 레거시 평면 화면은 런타임에서 평면 경로로 계속 렌더.

## 11. 단계화

- **Phase 0 (선행)**: §9 쿼리 메타데이터 엔드포인트 · `ScreenDefinition.Layout` 확장(init-only 노드) · 2단계 분리 파싱/폴백/MaxDepth · 라운드트립 테스트(정상·kind-not-first·unknown-kind·over-depth·Layout=null).
- **Phase 1**: SPA GrapesJS 에디터(잠금·StorageManager 매핑) · `LayoutRenderer` + 멀티 read 오케스트레이터 · 트림 컴포넌트 세트(§5) · 레이아웃 기반 검증.
- **Phase 2**: KPI·StatusBadge(+ 배지 value→style 모델) · 디자인 토큰 연동(보류 중 UX 파운데이션 리펙토링과 합류) · 레거시 평면 화면 "디자이너로 열기" 자동 변환 · 폼 다중 모델/컬럼 폭 등 고급.

## 12. 테스트 전략

- **직렬화 라운드트립(.NET)**: (a) Layout=null → `layout:null` 직렬화·null 복원, (b) Section>Row>Column>Grid/Form/Field/CommandButton 무손실, (c) `kind` 비-첫-속성 복원(init-only 검증), (d) unknown kind → 평면 폴백(전체 null 아님), (e) over-depth → 폴백, (f) `Id` 보존.
- **런타임(bUnit)**: 멀티 read 결과맵 라우팅, 위젯 빈/오류 상태, 공유 Model 양방향 바인딩, layout 기반 Validate, 권한 미보유 시 CommandButton 비활성.
- **통합**: 디자인→저장(PUT)→`/meta` 로드→렌더 e2e; write 명령 권한 게이트 403; SQLite 부트스트랩 경로.
- **SPA**: StorageManager 트리↔스키마 매핑 단위, 잠금(허용 외 블록 차단), 드롭다운 소스.

## 13. 미해결/추후 결정

- 컬럼 폭/정렬 메타데이터(Phase 2): `GridColumnDefinition` 확장 여부.
- StatusBadge value→style 매핑 모델(Phase 2): 스키마 테이블 vs 렌더러 기본 매핑.
- 다중 독립 폼 모델(Phase 2 이후): 현재는 화면당 공유 Model 1개.
- 레거시 평면→레이아웃 자동 변환 규칙(Phase 2).
