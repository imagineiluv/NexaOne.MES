# 디자이너 Phase 2 — 레거시 평면 정의→레이아웃 자동변환 (트랙 ③) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:subagent-driven-development. 체크박스 단계.

**Goal:** `layout`이 없는 레거시 평면 `ScreenDefinition`(Fields/Columns/QueryId/SaveQueryId)을 GrapesJS 디자이너에서 열 때, 평면 정의로부터 기본 `LayoutNode` 트리를 클라이언트에서 합성(`flatToLayout`)해 기존 화면을 WYSIWYG로 재편집 가능하게 한다.

**Architecture:** 순수 함수 `flatToLayout(dto): LayoutNode`를 `mapping.ts`에 추가(단방향), `ScreenEditor.tsx` 로드 분기를 "layout 있으면 복원 / 없으면 빈 section"에서 "layout 있으면 복원 / 없으면 `flatToLayout(dto)`"로 변경. vitest로 변환 규칙·엣지·라운드트립 검증. **런타임(Blazor /meta) 무변경** — 평면 정의는 이미 평면 경로로 렌더되므로 변환은 디자이너(편집) 측만 필요. C# 직렬화 권위 준수(병렬 직렬화기 금지).

**Tech Stack:** TypeScript(React SPA), vitest.

---

## 검증된 사실 (배경 워크플로 실측)

- **런타임 자동변환 불필요**: [MetaScreen.razor](../../../src/01.Web/NexaOne.Web.Components/Pages/Meta/MetaScreen.razor) `_definition.Layout is null`이면 평면 경로(MetaFormRenderer+MetaGridRenderer)로 직접 렌더. 레거시 평면 정의는 런타임에서 이미 정상 — 변환은 "디자이너로 열어 재편집"용만. **C# 변경 0.**
- **단방향 확정**: 디자이너 저장 시 `buildDefinitionJson`([mapping.ts](../../../src/01.Web/NexaOne.Spa/src/designer/mapping.ts))이 `fields:[], columns:null, queryId:null, saveQueryId:null` + `layout`을 쓰므로 저장 후 layout이 단일 출처 → 이중 출처 구조적 불가. flat→layout 역변환 불요. 단 생성된 layout은 `layoutToComponent`/`componentToLayout` 라운드트립 무손실이어야(디자이너 재저장 보존) — 8노드 무손실이 기존 보증이라 노드 형태만 따르면 자동 충족.
- **타깃 형태**: [InMemoryScreenDefinitionProvider.cs](../../../src/01.Web/NexaOne.Web.Components/Services/Meta/InMemoryScreenDefinitionProvider.cs) DEMO_LAYOUT = `Section(title)→Row→[Column(span=7){Grid}, Column(span=5){Form, Button}]`. 2열 케이스가 이를 따름.
- **로드 반환 확장 필요**: [api.ts](../../../src/01.Web/NexaOne.Spa/src/designer/api.ts) `loadDefinition`은 `{title, layout}`만 반환(평면 dto 버림), `parseDefinition`도 title·layout만. flatToLayout은 평면 필드가 필요 → **선택 A(권장)**: parseDefinition/loadDefinition이 `flat: ScreenDefinitionDto|null`도 반환. 이는 `api.test.ts`·`mapping.test.ts`의 기대값 조정을 수반(아래 명시).
- 디자이너 잠금: 루트=section 1개([ScreenEditor.tsx](../../../src/01.Web/NexaOne.Spa/src/features/ScreenEditor.tsx), [grapesConfig.ts](../../../src/01.Web/NexaOne.Spa/src/designer/grapesConfig.ts) nx-section.allowedParents:[]).

## 확정된 변환 규칙
- 그리드 측: `columns.length > 0`이면 `GridWidget{queryId, columns}`. (columns 비고 queryId만이면 그리드 미생성 — 컬럼 없는 그리드는 무의미.)
- 폼 측: `fields.length > 0`이면 `FormWidget{saveQueryId, fields:[FieldWidget...]}`; `saveQueryId` 있으면 뒤에 `ButtonWidget{label:'저장', command:saveQueryId}`.
- 둘 다 → 2열 `Row→[Column(7){Grid}, Column(5){Form,[Button]}]`. 하나만 → 단일열 `Column(12)`. 둘 다 없음 → 빈 `Section{children:[]}`.
- 모든 노드에 결정론적 id: `sec-{uid}`/`row-{uid}`/`col-grid-{uid}`/`col-form-{uid}`/`grid-{uid}`/`form-{uid}`/`btn-save-{uid}`/`fld-{field.key}`. uid=dto.uiId 또는 'gen'.
- 필드는 입력 FieldDefinition 그대로 보존(임의 기본값 주입 금지 — 권위는 C#).

## File Structure
- 수정: `src/01.Web/NexaOne.Spa/src/designer/mapping.ts`(flatToLayout 추가, parseDefinition 확장), `src/01.Web/NexaOne.Spa/src/designer/api.ts`(loadDefinition 반환 확장), `src/01.Web/NexaOne.Spa/src/features/ScreenEditor.tsx`(로드 분기), `src/01.Web/NexaOne.Spa/src/designer/__tests__/{mapping.test.ts, api.test.ts}`(테스트 추가/조정).

---

## Task 1: mapping.ts — flatToLayout + parseDefinition 확장

- [ ] **flatToLayout 추가** (import에 이미 ScreenDefinitionDto/GridColumnDefinition/FieldDefinition/FieldWidget 존재):
```ts
// 레거시 평면 정의(layout 없음)를 디자이너 편집용 기본 LayoutNode 트리로 합성(단방향, Phase 2).
// columns(1개↑)→그리드; fields(1개↑)→폼(+saveQueryId면 저장버튼); 둘 다면 2열(7/5), 하나면 12, 둘 다 없으면 빈 섹션.
export function flatToLayout(dto: ScreenDefinitionDto): LayoutNode {
  const uid = dto.uiId && dto.uiId.length > 0 ? dto.uiId : 'gen'
  const hasGrid = Array.isArray(dto.columns) && dto.columns.length > 0
  const hasForm = Array.isArray(dto.fields) && dto.fields.length > 0
  if (!hasGrid && !hasForm)
    return { kind: 'section', id: `sec-${uid}`, ...(dto.title ? { title: dto.title } : {}), children: [] }

  const cols: LayoutNode[] = []
  if (hasGrid) {
    const grid: LayoutNode = { kind: 'grid', id: `grid-${uid}`, queryId: dto.queryId ?? null, columns: dto.columns as GridColumnDefinition[] }
    cols.push({ kind: 'column', id: `col-grid-${uid}`, span: hasForm ? 7 : 12, children: [grid] })
  }
  if (hasForm) {
    const fields: FieldWidget[] = (dto.fields).map((f: FieldDefinition) => ({ kind: 'field', id: `fld-${f.key}`, fieldKey: f.key, field: f }))
    const formChildren: LayoutNode[] = [{ kind: 'form', id: `form-${uid}`, saveQueryId: dto.saveQueryId ?? null, fields }]
    if (dto.saveQueryId) formChildren.push({ kind: 'commandButton', id: `btn-save-${uid}`, label: '저장', command: dto.saveQueryId })
    cols.push({ kind: 'column', id: `col-form-${uid}`, span: hasGrid ? 5 : 12, children: formChildren })
  }
  return { kind: 'section', id: `sec-${uid}`, ...(dto.title ? { title: dto.title } : {}), children: [{ kind: 'row', id: `row-${uid}`, children: cols }] }
}
```
- [ ] **parseDefinition 확장** (flat 포함):
```ts
export function parseDefinition(json: string): { title: string; layout: LayoutNode | null; flat: ScreenDefinitionDto | null } {
  try {
    const dto = JSON.parse(json) as Partial<ScreenDefinitionDto>
    const flat: ScreenDefinitionDto = {
      uiId: dto.uiId ?? '', title: dto.title ?? '',
      fields: Array.isArray(dto.fields) ? dto.fields : [],
      columns: dto.columns ?? null, queryId: dto.queryId ?? null, saveQueryId: dto.saveQueryId ?? null,
      layout: (dto.layout as LayoutNode | undefined) ?? null,
    }
    return { title: flat.title, layout: flat.layout ?? null, flat }
  } catch {
    return { title: '', layout: null, flat: null }
  }
}
```

## Task 2: api.ts — loadDefinition 반환 확장
import에 `ScreenDefinitionDto` 추가. 반환을 `{ title, layout, flat }`로:
```ts
export async function loadDefinition(uiId: string): Promise<{ title: string; layout: LayoutNode | null; flat: ScreenDefinitionDto | null }> {
  const rows = await apiFetch<ScreenDefRow[]>('/api/v1/query/SYS.GetScreenDefinition', { method: 'POST', body: JSON.stringify({ uiId }) })
  const json = rows[0]?.DEFINITION_JSON
  if (!json) return { title: rows[0]?.TITLE ?? '', layout: null, flat: null }
  return parseDefinition(json)
}
```

## Task 3: ScreenEditor.tsx — 로드 분기
import에 `flatToLayout` 추가. 두 then 가지의 fallback 객체에 `flat: null` 포함, 로드 then에서 `layout ?? (flat ? flatToLayout(flat) : null)`:
```ts
      .then(({ title: loaded, layout, flat }) => {
        if (disposed) return
        setTitle(loaded || (uiId ?? ''))
        const effective: LayoutNode | null = layout ?? (flat ? flatToLayout(flat) : null)
        const root: GrapesNode = effective ? layoutToComponent(effective) : { type: 'nx-section', attributes: {}, components: [] }
        editor.setComponents([root] as ComponentAdd)
        setStatus('준비됨')
      })
```
그리고 uiId 없을 때 분기(`uiId ? loadDefinition(uiId) : ...`)의 else를 `{ title: '', layout: null as LayoutNode | null, flat: null }`로(2곳: disposed 가지·정상 가지).

## Task 4: 테스트
- [ ] mapping.test.ts에 flatToLayout describe 추가(그리드만 span12 / 폼만+버튼 span12 / 폼+saveQueryId없음 버튼없음 / 둘다 2열 7·5 / 둘다없음 빈섹션 / columns비고queryId만 빈섹션 / 5속성field 라운드트립 무손실 / 모든노드 id). import에 `flatToLayout`, `ScreenDefinitionDto` 추가. `dto(p)` 헬퍼로 부분 생성.
- [ ] 기존 기대값 조정: mapping.test.ts의 `parseDefinition` toEqual 비교 2곳에 `flat` 포함(깨진JSON→`{title:'',layout:null,flat:null}`; 레거시 평면→`toMatchObject({title,layout:null})`로 완화 또는 flat 명시). api.test.ts의 `loadDefinition` 빈결과 기대 `{title:'',layout:null}`→`{title:'',layout:null,flat:null}`.

## 검증 (작업 디렉터리 src/01.Web/NexaOne.Spa)
```
npx vitest run src/designer/__tests__/mapping.test.ts
npx vitest run src/designer/__tests__/api.test.ts
npm test
npx tsc -b
```
전부 통과(신규 변환 8 + 기존 회귀), tsc 0 errors.

## 커밋/병합
BOM-free. `git add -A` 금지(submodules/NexusLogic 더티) — mapping.ts/api.ts/ScreenEditor.tsx/mapping.test.ts/api.test.ts만. main ff-merge, push 안 함. Co-Authored-By 트레일러.

## 주의/리스크
- parseDefinition/loadDefinition 반환 확장의 파급(api.test.ts·mapping.test.ts 기대값) — 위에 명시.
- LayoutNode 유니온 좁히기: 각 노드를 `LayoutNode` 타입 변수/구조적 리터럴로 push(프로덕션 코드 `as any` 금지; 테스트 트리 탐색은 `Extract<>` 사용).
- field 라운드트립 무손실은 5속성(key/label/type/required/readOnly/options) 명시 시만 보증(JSON.stringify undefined 누락) — 단방향 자동변환의 의도된 한계(테스트는 무손실 케이스만).
- 동일 field.key 중복 시 `fld-{key}` id 충돌 가능하나 레거시는 key 유일 전제(폼 모델 dict 키) — dedup 불요.
