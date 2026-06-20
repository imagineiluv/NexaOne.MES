# GrapesJS 화면 디자이너 SPA (Phase 5b) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** NexaOne.Spa(React)에 `/spa/designer/:uiId?` WYSIWYG 화면 디자이너를 추가한다 — 8개 잠금 블록을 캔버스에 배치 → `LayoutNode` 트리로 매핑 → 게이트웨이 `SYS.UpsertScreenDefinition`으로 저장하고, `SYS.GetScreenDefinition`으로 다시 로드해 라운드트립한다.

**Architecture:** 디자이너 코어(LayoutNode↔GrapesJS 컴포넌트 매핑, 정의 직렬화, API 클라이언트)는 **브라우저 비의존 순수 함수**로 분리해 vitest로 엄격히 검증한다. GrapesJS 에디터 자체(캔버스·블록·트레이트·잠금)는 얇은 React 글루(`ScreenEditor.tsx`)로 감싸고, 설정 빌더는 단위 테스트하되 캔버스 드래그&드롭은 수동 검증한다. C# `ScreenDefinitionJson`이 직렬화 형식의 유일 권위이며, SPA는 그 형식(§5 예시 JSON)을 타깃으로 매핑만 한다(병렬 직렬화기 금지 — 매핑은 GrapesJS↔스키마 변환일 뿐).

**Tech Stack:** React 18 + TypeScript 5.5 + Vite 6, grapesjs(바닐라 JS, 자체 타입 포함), react-router-dom v6, vitest + jsdom + @testing-library/react. 백엔드 무변경(Phase 5a 게이트웨이 엔드포인트 재사용).

---

## 검증된 통합 계약 (실제 코드 기준, 2026-06-20)

이 계획의 모든 통합 지점은 현재 코드에서 직접 확인되었다. 구현자는 이 사실에 의존해도 된다.

- **로드**: `POST /api/v1/query/SYS.GetScreenDefinition`, 본문 `{ "uiId": "<id>" }` → `200 [{ "UI_ID": "...", "TITLE": "...", "DEFINITION_JSON": "<직렬화된 ScreenDefinition 문자열>" }]`. 열 이름은 **대문자**(SQL 그대로). 없으면 `[]`(빈 배열).
  - 출처: [QueryGatewayController.cs:34-47](../../../src/00.Main/NexaOne.Server/Gateway/QueryGatewayController.cs#L34-L47), [db/queries/sqlite/SYS.xml:4-8](../../../db/queries/sqlite/SYS.xml#L4-L8).
- **저장**: `POST /api/v1/command/SYS.UpsertScreenDefinition`, 본문 `{ "uiId": "...", "title": "...", "definitionJson": "<문자열>" }` → `200 { "affected": 1 }`. `@currentUser`/`@utcNow`는 **서버 주입**(클라이언트가 보내도 무시·덮어씀). 권한 `sys:manage` 필요 — 미보유 시 `403`.
  - 출처: [QueryGatewayController.cs:55-68](../../../src/00.Main/NexaOne.Server/Gateway/QueryGatewayController.cs#L55-L68), [db/queries/sqlite/SYS.xml:14-22](../../../db/queries/sqlite/SYS.xml#L14-L22).
- **쿼리 카탈로그**: `GET /api/v1/sys/queries` → `200 [{ "id": "MDM.PlantList", "isWrite": false, "requiredPermission": null }, ...]`. 권한 `sys:manage` 필요(미보유 `403`). camelCase 직렬화.
  - 출처: [QueryCatalogController.cs:22-34](../../../src/00.Main/NexaOne.Server/Gateway/QueryCatalogController.cs#L22-L34).
- **`DEFINITION_JSON` 내용** = 직렬화된 `ScreenDefinition`: `{ uiId, title, fields, columns, queryId, saveQueryId, layout }`(camelCase, `FieldType`는 문자열 enum, `layout`은 다형 트리 — discriminator `kind`). §5 예시가 골든 픽스처다.
  - C# 형식 권위: [ScreenDefinitionJson.cs](../../../src/01.Web/NexaOne.Web.Components/Services/Meta/ScreenDefinitionJson.cs), 스키마: [grapesjs-screen-designer-design.md §5](../specs/2026-06-19-grapesjs-screen-designer-design.md).
- **LayoutNode 8종**(discriminator `kind`): `section`(Title,Children) / `row`(Children) / `column`(Span,Children) / `grid`(QueryId,Columns) / `form`(SaveQueryId,Fields) / `field`(FieldKey,Field) / `commandButton`(Label,Command,RequiredPermission) / `text`(Text,IsLabel). 공통: `Id`, `RequiredPermission`(UX 힌트 전용).
- **SPA 현황**: 상태 기반 라우팅(라우터 없음), `apiFetch`(Bearer+401 single-flight refresh)는 [client.ts](../../../src/01.Web/NexaOne.Spa/src/api/client.ts), 세션은 모듈 전역, `hasPermission(token, perm)`은 [jwt.ts](../../../src/01.Web/NexaOne.Spa/src/auth/jwt.ts). 엔트리 [main.tsx](../../../src/01.Web/NexaOne.Spa/src/main.tsx) → `ErrorBoundary` → `App`. 빌드 출력(`wwwroot/spa`)은 **gitignored**(번들 커밋 불필요). Blazor `/meta` 런타임 렌더러(`LayoutRenderer`)는 Phase 0/1a에서 이미 완성·병합됨 → 이 Phase는 **디자이너(생산자)** 절반만 추가.

## File Structure

신규 디렉터리 `src/designer/`에 브라우저 비의존 코어를 모은다. GrapesJS 글루만 React/DOM에 의존.

- `src/designer/layout.ts` — LayoutNode TS 판별 유니온 타입 + 정의 타입(C# §5 미러). 로직 없음.
- `src/designer/mapping.ts` — **순수 매핑**: `layoutToComponent`/`componentToLayout`(GrapesJS 컴포넌트 JSON ↔ LayoutNode), `buildDefinitionJson`(LayoutNode → definitionJson 문자열), `parseDefinition`(DEFINITION_JSON 문자열 → {title, layout}, 관용적).
- `src/designer/api.ts` — 게이트웨이 클라이언트: `loadDefinition`/`saveDefinition`/`listQueries`. `apiFetch` 재사용.
- `src/designer/grapesConfig.ts` — `buildEditorConfig()`, `BLOCK_DEFS`, `COMPONENT_TYPE_DEFS`, `buildTraitDefs(queries)` — 잠금·블록·트레이트 **순수 데이터/설정**(단위 테스트 대상).
- `src/features/ScreenEditor.tsx` — GrapesJS를 마운트하는 얇은 React 컴포넌트(글루: 코어 호출 + 캔버스 init).
- `src/designer/__tests__/*.test.ts(x)` — vitest 단위 테스트.
- 수정: `package.json`(deps/scripts), 신규 `vitest.config.ts`, `src/main.tsx`(라우터 래핑), `src/App.tsx`(라우트), `src/features/Dashboard.tsx`(디자이너 진입 링크).

---

## Task 1: SPA 테스트 하니스 + Phase 5b 의존성

**Files:**
- Modify: `src/01.Web/NexaOne.Spa/package.json`
- Create: `src/01.Web/NexaOne.Spa/vitest.config.ts`
- Create: `src/01.Web/NexaOne.Spa/src/test/setup.ts`
- Create: `src/01.Web/NexaOne.Spa/src/designer/__tests__/smoke.test.ts`

- [ ] **Step 1: package.json에 의존성·스크립트 추가**

`dependencies`에 추가, `devDependencies`에 추가, `scripts`에 `test`/`test:watch` 추가. 최종 파일:

```json
{
  "name": "nexaone-spa",
  "private": true,
  "version": "0.1.0",
  "type": "module",
  "description": "NexaMes Pro-Code 공존 SPA (React) — Blazor와 동일 API(REST/SignalR/JWT) 위에서 동작 (Frontend-Coexistence Phase 2)",
  "scripts": {
    "dev": "vite",
    "build": "tsc -b && vite build",
    "preview": "vite preview",
    "test": "vitest run",
    "test:watch": "vitest",
    "gen:api": "nswag run nswag.json"
  },
  "dependencies": {
    "@microsoft/signalr": "^8.0.7",
    "grapesjs": "^0.21.13",
    "react": "^18.3.1",
    "react-dom": "^18.3.1",
    "react-router-dom": "^6.28.0"
  },
  "devDependencies": {
    "@testing-library/jest-dom": "^6.4.8",
    "@testing-library/react": "^16.0.1",
    "@types/node": "^25.9.3",
    "@types/react": "^18.3.3",
    "@types/react-dom": "^18.3.0",
    "@vitejs/plugin-react": "^4.3.4",
    "jsdom": "^25.0.1",
    "nswag": "^14.1.0",
    "typescript": "^5.5.4",
    "vite": "^6.0.0",
    "vitest": "^2.1.8"
  }
}
```

- [ ] **Step 2: 의존성 설치**

Run: `cd src/01.Web/NexaOne.Spa && npm install`
Expected: 성공(npm 레지스트리 도달 확인됨). 설치 후 `node_modules/grapesjs`, `node_modules/react-router-dom`, `node_modules/vitest` 존재.
주의(오프라인 시): 설치가 실패하면 BLOCKED로 보고하라 — 임의 버전 대체 금지.

- [ ] **Step 3: vitest 설정 작성**

`vite.config.ts`는 빌드용으로 건드리지 않고, 별도 `vitest.config.ts`를 만든다(jsdom 환경 + setup + globals).

```ts
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

// 디자이너 코어(매핑/직렬화/API)는 브라우저 비의존 순수 로직 → jsdom로 충분.
// GrapesJS 캔버스 자체는 단위 테스트 비대상(수동/플레이wright). 설정 빌더는 순수라 테스트한다.
export default defineConfig({
  plugins: [react()],
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
```

- [ ] **Step 4: 테스트 setup 작성**

```ts
// @testing-library/jest-dom 매처(toBeInTheDocument 등) 등록.
import '@testing-library/jest-dom/vitest'
```

- [ ] **Step 5: 스모크 테스트로 하니스 확인**

```ts
import { describe, it, expect } from 'vitest'

describe('vitest 하니스', () => {
  it('실행된다', () => {
    expect(1 + 1).toBe(2)
  })
})
```

- [ ] **Step 6: 테스트 실행 — 통과 확인**

Run: `cd src/01.Web/NexaOne.Spa && npm test`
Expected: 1 passed. (jsdom 환경 로드, setup 적용)

- [ ] **Step 7: 타입 빌드 회귀 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx tsc -b`
Expected: 0 errors. (테스트 파일이 `include:["src"]`에 들어가도 vitest 타입이 해소돼 통과)
주의: `vitest/globals` 타입이 필요하면 `tsconfig.json`의 `compilerOptions.types`에 `"vitest/globals"`를 추가하라(스모크 테스트가 `describe/it/expect`를 import하므로 globals 불필요할 수 있음 — tsc 에러 시에만 추가).

- [ ] **Step 8: 커밋**

```
git add src/01.Web/NexaOne.Spa/package.json src/01.Web/NexaOne.Spa/package-lock.json src/01.Web/NexaOne.Spa/vitest.config.ts src/01.Web/NexaOne.Spa/src/test/ src/01.Web/NexaOne.Spa/src/designer/
git commit -F <BOM-free-msg>
```
커밋 메시지: `chore(spa): Phase 5b 디자이너 의존성(grapesjs·react-router·vitest) + 테스트 하니스`
주의: `git add -A` 금지(submodules/NexusLogic 더티). `node_modules`는 gitignored(추가 금지).

---

## Task 2: LayoutNode 타입 + 순수 매핑 코어 (디자이너의 심장)

**Files:**
- Create: `src/01.Web/NexaOne.Spa/src/designer/layout.ts`
- Create: `src/01.Web/NexaOne.Spa/src/designer/mapping.ts`
- Test: `src/01.Web/NexaOne.Spa/src/designer/__tests__/mapping.test.ts`

**스코프 메모:** GrapesJS 컴포넌트의 직렬화 형태(`component.toJSON()`)는 `{ type, attributes?, components? }`다. 매핑을 순수하게 유지하려고 이 형태를 `GrapesNode` 인터페이스로 정의하고, 매핑 함수는 실제 에디터 없이 plain 객체만 다룬다. 바인딩(queryId/span/필드 등)은 안정 키의 `attributes`에 저장한다(라운드트립 정체성). FormWidget의 필드는 `nx-field` **자식 컴포넌트**로(트리 1:1), GridWidget의 컬럼은 직렬화 부담을 줄이려 `data-columns` JSON 속성으로 저장한다(컬럼은 8블록 팔레트 비대상).

- [ ] **Step 1: layout.ts 작성 (C# §5 미러 타입, 로직 없음)**

```ts
// C# ScreenDefinition/LayoutNode(§5)의 TS 미러. 직렬화 형식의 권위는 C# ScreenDefinitionJson이며
// 여기서는 그 camelCase 형태를 타깃으로 한다(병렬 직렬화기 금지 — mapping.ts는 GrapesJS↔스키마 변환만).
export type FieldType = 'Text' | 'Number' | 'Boolean' | 'Date' | 'Select'

export interface FieldDefinition {
  key: string
  label: string
  type: FieldType
  required?: boolean
  readOnly?: boolean
  options?: string[] | null
}

export interface GridColumnDefinition {
  key: string
  caption: string
  visible?: boolean
}

// 판별 유니온 — kind가 discriminator. 컨테이너는 children, 위젯은 leaf.
export type LayoutNode =
  | SectionNode | RowNode | ColumnNode
  | GridWidget | FormWidget | FieldWidget | ButtonWidget | TextWidget

interface NodeBase {
  id?: string
  requiredPermission?: string | null   // UX 힌트 전용(서버 게이트 아님)
}
export interface SectionNode extends NodeBase { kind: 'section'; title?: string; children?: LayoutNode[] }
export interface RowNode     extends NodeBase { kind: 'row'; children?: LayoutNode[] }
export interface ColumnNode  extends NodeBase { kind: 'column'; span: number; children?: LayoutNode[] }
export interface GridWidget  extends NodeBase { kind: 'grid'; queryId?: string | null; columns?: GridColumnDefinition[] }
export interface FormWidget  extends NodeBase { kind: 'form'; saveQueryId?: string | null; fields?: FieldWidget[] }
export interface FieldWidget extends NodeBase { kind: 'field'; fieldKey?: string | null; field?: FieldDefinition | null }
export interface ButtonWidget extends NodeBase { kind: 'commandButton'; label: string; command?: string | null }
export interface TextWidget  extends NodeBase { kind: 'text'; text: string; isLabel?: boolean }

// 직렬화된 ScreenDefinition(DEFINITION_JSON 내용)
export interface ScreenDefinitionDto {
  uiId: string
  title: string
  fields: FieldDefinition[]
  columns?: GridColumnDefinition[] | null
  queryId?: string | null
  saveQueryId?: string | null
  layout?: LayoutNode | null
}

// GrapesJS 컴포넌트 직렬화 형태(component.toJSON()) — 매핑이 다루는 중립 형태.
export interface GrapesNode {
  type?: string
  attributes?: Record<string, unknown>
  components?: GrapesNode[]
}
```

- [ ] **Step 2: 실패 테스트 작성 (라운드트립 + 직렬화 + 관용 파싱)**

`mapping.test.ts`:

```ts
import { describe, it, expect } from 'vitest'
import type { LayoutNode } from '../layout'
import { layoutToComponent, componentToLayout, buildDefinitionJson, parseDefinition } from '../mapping'

// §5 골든 픽스처(그리드 + 폼 + 저장 버튼)
const golden: LayoutNode = {
  kind: 'section', id: 'sec-root', title: '공장 마스터',
  children: [{
    kind: 'row', id: 'row-1', children: [
      { kind: 'column', id: 'col-1', span: 7, children: [
        { kind: 'grid', id: 'grid-plants', queryId: 'MDM.PlantList',
          columns: [{ key: 'PLANT_ID', caption: '공장 ID', visible: true }] },
      ] },
      { kind: 'column', id: 'col-2', span: 5, children: [
        { kind: 'form', id: 'form-plant', saveQueryId: 'MDM.CreatePlant', fields: [
          { kind: 'field', id: 'fld-1', fieldKey: 'plantId',
            field: { key: 'plantId', label: '공장 ID', type: 'Text', required: true, readOnly: false, options: null } },
        ] },
        { kind: 'commandButton', id: 'btn-1', label: '저장', command: 'MDM.CreatePlant', requiredPermission: 'mdm:manage' },
      ] },
    ],
  }],
}

describe('LayoutNode ↔ GrapesJS 매핑', () => {
  it('레이아웃→컴포넌트→레이아웃 라운드트립 무손실', () => {
    const comp = layoutToComponent(golden)
    const back = componentToLayout(comp)
    expect(back).toEqual(golden)
  })

  it('각 노드의 GrapesJS type이 kind에 대응', () => {
    expect(layoutToComponent(golden).type).toBe('nx-section')
    const grid = layoutToComponent({ kind: 'grid', id: 'g', queryId: 'Q' })
    expect(grid.type).toBe('nx-grid')
    expect(grid.attributes!['data-query-id']).toBe('Q')
  })

  it('미지 type은 null로 격리(전체 트리 깨뜨리지 않음)', () => {
    const back = componentToLayout({ type: 'textnode', components: [] })
    expect(back).toBeNull()
  })
})

describe('정의 직렬화', () => {
  it('buildDefinitionJson은 §5 형식(camelCase·평면 필드 빈 배열·layout 포함)을 만든다', () => {
    const json = buildDefinitionJson('PLANT_MGMT', '공장 관리', golden)
    const parsed = JSON.parse(json)
    expect(parsed.uiId).toBe('PLANT_MGMT')
    expect(parsed.title).toBe('공장 관리')
    expect(parsed.fields).toEqual([])
    expect(parsed.layout.kind).toBe('section')
    expect(parsed.layout.children[0].children[0].children[0].queryId).toBe('MDM.PlantList')
  })

  it('parseDefinition은 DEFINITION_JSON에서 title·layout을 복원', () => {
    const json = buildDefinitionJson('X', '타이틀', golden)
    const { title, layout } = parseDefinition(json)
    expect(title).toBe('타이틀')
    expect(layout).toEqual(golden)
  })

  it('parseDefinition은 깨진 JSON에 null layout 반환(throw 금지)', () => {
    expect(parseDefinition('not json')).toEqual({ title: '', layout: null })
  })

  it('parseDefinition은 layout 없는 레거시 평면 정의에 layout=null', () => {
    const json = JSON.stringify({ uiId: 'L', title: '레거시', fields: [], columns: null })
    expect(parseDefinition(json)).toEqual({ title: '레거시', layout: null })
  })
})
```

- [ ] **Step 3: 테스트 실패 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/mapping.test.ts`
Expected: FAIL — `layoutToComponent is not a function`(mapping.ts 미구현).

- [ ] **Step 4: mapping.ts 구현**

```ts
import type {
  LayoutNode, GrapesNode, FieldDefinition, GridColumnDefinition,
  FieldWidget, ScreenDefinitionDto,
} from './layout'

// kind ↔ GrapesJS component type. 8종 1:1.
const KIND_TO_TYPE: Record<LayoutNode['kind'], string> = {
  section: 'nx-section', row: 'nx-row', column: 'nx-column', grid: 'nx-grid',
  form: 'nx-form', field: 'nx-field', commandButton: 'nx-button', text: 'nx-text',
}
const TYPE_TO_KIND: Record<string, LayoutNode['kind']> = Object.fromEntries(
  Object.entries(KIND_TO_TYPE).map(([k, v]) => [v, k as LayoutNode['kind']]),
) as Record<string, LayoutNode['kind']>

function attrsBase(node: LayoutNode): Record<string, unknown> {
  const a: Record<string, unknown> = {}
  if (node.id != null) a['data-node-id'] = node.id
  if (node.requiredPermission != null) a['data-required-permission'] = node.requiredPermission
  return a
}

/** LayoutNode → GrapesJS 컴포넌트 정의(재귀). 바인딩은 안정 키 attributes에 저장. */
export function layoutToComponent(node: LayoutNode): GrapesNode {
  const attributes = attrsBase(node)
  const comp: GrapesNode = { type: KIND_TO_TYPE[node.kind], attributes }
  switch (node.kind) {
    case 'section':
      if (node.title != null) attributes['data-title'] = node.title
      comp.components = (node.children ?? []).map(layoutToComponent)
      break
    case 'row':
      comp.components = (node.children ?? []).map(layoutToComponent)
      break
    case 'column':
      attributes['data-span'] = node.span
      comp.components = (node.children ?? []).map(layoutToComponent)
      break
    case 'grid':
      if (node.queryId != null) attributes['data-query-id'] = node.queryId
      if (node.columns != null) attributes['data-columns'] = JSON.stringify(node.columns)
      break
    case 'form':
      if (node.saveQueryId != null) attributes['data-save-query-id'] = node.saveQueryId
      comp.components = (node.fields ?? []).map(layoutToComponent)
      break
    case 'field':
      if (node.fieldKey != null) attributes['data-field-key'] = node.fieldKey
      if (node.field != null) attributes['data-field'] = JSON.stringify(node.field)
      break
    case 'commandButton':
      attributes['data-label'] = node.label
      if (node.command != null) attributes['data-command'] = node.command
      break
    case 'text':
      attributes['data-text'] = node.text
      if (node.isLabel) attributes['data-is-label'] = true
      break
  }
  return comp
}

function str(a: Record<string, unknown> | undefined, k: string): string | undefined {
  const v = a?.[k]
  return typeof v === 'string' ? v : undefined
}
function jsonAttr<T>(a: Record<string, unknown> | undefined, k: string): T | undefined {
  const v = a?.[k]
  if (typeof v !== 'string') return undefined
  try { return JSON.parse(v) as T } catch { return undefined }
}

/** GrapesJS 컴포넌트 JSON → LayoutNode(재귀). 미지 type은 null(격리). */
export function componentToLayout(comp: GrapesNode): LayoutNode | null {
  const kind = comp.type ? TYPE_TO_KIND[comp.type] : undefined
  if (!kind) return null
  const a = comp.attributes
  const id = str(a, 'data-node-id')
  const requiredPermission = str(a, 'data-required-permission')
  const childNodes = (comp.components ?? []).map(componentToLayout).filter((n): n is LayoutNode => n !== null)
  const base = { ...(id != null ? { id } : {}), ...(requiredPermission != null ? { requiredPermission } : {}) }
  switch (kind) {
    case 'section': {
      const title = str(a, 'data-title')
      return { kind, ...base, ...(title != null ? { title } : {}), children: childNodes }
    }
    case 'row':
      return { kind, ...base, children: childNodes }
    case 'column': {
      const span = typeof a?.['data-span'] === 'number' ? (a['data-span'] as number) : Number(a?.['data-span'] ?? 12)
      return { kind, ...base, span, children: childNodes }
    }
    case 'grid': {
      const queryId = str(a, 'data-query-id')
      const columns = jsonAttr<GridColumnDefinition[]>(a, 'data-columns')
      return { kind, ...base, ...(queryId != null ? { queryId } : {}), ...(columns != null ? { columns } : {}) }
    }
    case 'form': {
      const saveQueryId = str(a, 'data-save-query-id')
      const fields = childNodes.filter((n): n is FieldWidget => n.kind === 'field')
      return { kind, ...base, ...(saveQueryId != null ? { saveQueryId } : {}), fields }
    }
    case 'field': {
      const fieldKey = str(a, 'data-field-key')
      const field = jsonAttr<FieldDefinition>(a, 'data-field')
      return { kind, ...base, ...(fieldKey != null ? { fieldKey } : {}), ...(field != null ? { field } : {}) }
    }
    case 'commandButton': {
      const command = str(a, 'data-command')
      return { kind, ...base, label: str(a, 'data-label') ?? '', ...(command != null ? { command } : {}) }
    }
    case 'text':
      return { kind, ...base, text: str(a, 'data-text') ?? '', ...(a?.['data-is-label'] ? { isLabel: true } : {}) }
  }
}

/** LayoutNode → definitionJson 문자열(§5 형식). 평면 필드는 빈 배열(레이아웃-우선 디자이너). */
export function buildDefinitionJson(uiId: string, title: string, layout: LayoutNode | null): string {
  const dto: ScreenDefinitionDto = {
    uiId, title, fields: [], columns: null, queryId: null, saveQueryId: null,
    layout: layout ?? null,
  }
  return JSON.stringify(dto)
}

/** DEFINITION_JSON 문자열 → {title, layout}. 깨진/평면 정의는 layout=null로 관용 처리(throw 금지). */
export function parseDefinition(json: string): { title: string; layout: LayoutNode | null } {
  try {
    const dto = JSON.parse(json) as Partial<ScreenDefinitionDto>
    return { title: dto.title ?? '', layout: (dto.layout as LayoutNode | undefined) ?? null }
  } catch {
    return { title: '', layout: null }
  }
}
```

- [ ] **Step 5: 테스트 통과 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/mapping.test.ts`
Expected: 모든 테스트 PASS. 라운드트립 `toEqual(golden)`이 핵심 — `field`의 키 순서 무관, 값 동일.
주의: `toEqual`은 객체 키 순서 무관·깊은 동등. `componentToLayout`이 옵션 키를 조건부로만 넣으므로 golden과 정확히 일치해야 한다(예: `column`에 `requiredPermission` 없으면 결과에도 없어야 함).

- [ ] **Step 6: 커밋**

커밋 메시지: `feat(spa): 디자이너 코어 — LayoutNode 타입 + GrapesJS↔스키마 순수 매핑(라운드트립 검증)`

---

## Task 3: 게이트웨이 API 클라이언트 (로드/저장/카탈로그)

**Files:**
- Create: `src/01.Web/NexaOne.Spa/src/designer/api.ts`
- Test: `src/01.Web/NexaOne.Spa/src/designer/__tests__/api.test.ts`

- [ ] **Step 1: 실패 테스트 작성 (요청 형태·응답 파싱, fetch 모킹)**

`api.test.ts`:

```ts
import { describe, it, expect, vi, beforeEach } from 'vitest'
import { loadDefinition, saveDefinition, listQueries } from '../api'
import type { LayoutNode } from '../layout'

// apiFetch는 전역 fetch를 쓴다(client.ts). fetch를 모킹해 요청 형태/응답 파싱을 검증.
const fetchMock = vi.fn()
beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})
function ok(body: unknown) {
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }))
}

const layout: LayoutNode = { kind: 'section', id: 's', children: [] }

describe('디자이너 API 클라이언트', () => {
  it('loadDefinition은 query 엔드포인트를 호출하고 DEFINITION_JSON을 파싱', async () => {
    const defJson = JSON.stringify({ uiId: 'X', title: '로드됨', fields: [], layout })
    fetchMock.mockReturnValueOnce(ok([{ UI_ID: 'X', TITLE: '로드됨', DEFINITION_JSON: defJson }]))
    const res = await loadDefinition('X')
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toContain('/api/v1/query/SYS.GetScreenDefinition')
    expect(JSON.parse(init.body)).toEqual({ uiId: 'X' })
    expect(res.title).toBe('로드됨')
    expect(res.layout).toEqual(layout)
  })

  it('loadDefinition은 빈 결과(신규 화면)에 layout=null', async () => {
    fetchMock.mockReturnValueOnce(ok([]))
    const res = await loadDefinition('NEW')
    expect(res).toEqual({ title: '', layout: null })
  })

  it('saveDefinition은 command 엔드포인트에 {uiId,title,definitionJson}을 보낸다', async () => {
    fetchMock.mockReturnValueOnce(ok({ affected: 1 }))
    const affected = await saveDefinition('X', '저장', layout)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toContain('/api/v1/command/SYS.UpsertScreenDefinition')
    const body = JSON.parse(init.body)
    expect(body.uiId).toBe('X')
    expect(body.title).toBe('저장')
    expect(typeof body.definitionJson).toBe('string')
    expect(JSON.parse(body.definitionJson).layout).toEqual(layout)  // 서버 저장 형식 = §5 직렬화
    expect(affected).toBe(1)
  })

  it('listQueries는 카탈로그를 read/write로 분리', async () => {
    fetchMock.mockReturnValueOnce(ok([
      { id: 'MDM.PlantList', isWrite: false, requiredPermission: null },
      { id: 'MDM.CreatePlant', isWrite: true, requiredPermission: 'mdm:manage' },
    ]))
    const { reads, writes } = await listQueries()
    expect(reads).toEqual(['MDM.PlantList'])
    expect(writes).toEqual([{ id: 'MDM.CreatePlant', requiredPermission: 'mdm:manage' }])
  })
})
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/api.test.ts`
Expected: FAIL — `loadDefinition is not a function`.

- [ ] **Step 3: api.ts 구현**

```ts
import { apiFetch } from '../api/client'
import type { LayoutNode } from './layout'
import { buildDefinitionJson, parseDefinition } from './mapping'

// 게이트웨이 query 결과 행(열 이름 대문자 — SQL 그대로).
interface ScreenDefRow { UI_ID?: string; TITLE?: string; DEFINITION_JSON?: string }
interface AffectedRows { affected: number }
interface QueryDescriptor { id: string; isWrite: boolean; requiredPermission: string | null }

/** 화면정의 로드 — 없으면 {title:'', layout:null}(신규). */
export async function loadDefinition(uiId: string): Promise<{ title: string; layout: LayoutNode | null }> {
  const rows = await apiFetch<ScreenDefRow[]>('/api/v1/query/SYS.GetScreenDefinition', {
    method: 'POST',
    body: JSON.stringify({ uiId }),
  })
  const json = rows[0]?.DEFINITION_JSON
  if (!json) return { title: rows[0]?.TITLE ?? '', layout: null }
  return parseDefinition(json)
}

/** 화면정의 저장 — definitionJson은 §5 형식으로 직렬화해 command 게이트웨이로 전송. 영향 행 수 반환. */
export async function saveDefinition(uiId: string, title: string, layout: LayoutNode | null): Promise<number> {
  const definitionJson = buildDefinitionJson(uiId, title, layout)
  const res = await apiFetch<AffectedRows>('/api/v1/command/SYS.UpsertScreenDefinition', {
    method: 'POST',
    body: JSON.stringify({ uiId, title, definitionJson }),
  })
  return res.affected
}

/** 쿼리 카탈로그 — 디자이너 드롭다운 소스. read/write 분리(grid=read, button/form=write). */
export async function listQueries(): Promise<{ reads: string[]; writes: { id: string; requiredPermission: string | null }[] }> {
  const items = await apiFetch<QueryDescriptor[]>('/api/v1/sys/queries', { method: 'GET' })
  return {
    reads: items.filter(q => !q.isWrite).map(q => q.id),
    writes: items.filter(q => q.isWrite).map(q => ({ id: q.id, requiredPermission: q.requiredPermission })),
  }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/api.test.ts`
Expected: 모든 테스트 PASS.
주의: `apiFetch`는 `Content-Type: application/json`을 항상 설정하고 GET에도 본문 없이 동작한다. GET 호출 시 `body` 미전달 확인.

- [ ] **Step 5: 커밋**

커밋 메시지: `feat(spa): 디자이너 게이트웨이 클라이언트(SYS.Get/Upsert/queries) + 모킹 단위 테스트`

---

## Task 4: GrapesJS 설정(잠금·블록·트레이트) + ScreenEditor 컴포넌트

**Files:**
- Create: `src/01.Web/NexaOne.Spa/src/designer/grapesConfig.ts`
- Create: `src/01.Web/NexaOne.Spa/src/features/ScreenEditor.tsx`
- Test: `src/01.Web/NexaOne.Spa/src/designer/__tests__/grapesConfig.test.ts`

**스코프 메모:** `grapesConfig.ts`는 **순수 데이터/설정**(블록 목록·컴포넌트 type 정의·트레이트 정의·init 옵션)이라 단위 테스트한다. 실제 GrapesJS 인스턴스 생성·캔버스 렌더는 `ScreenEditor.tsx` 글루에서만 일어나고 **수동 검증** 대상(jsdom에서 GrapesJS 캔버스는 신뢰 불가). 잠금 = 기본 블록/RTE/스타일 매니저 비노출, 8개 type만 등록, 중첩 규칙(Section⊃Row, Row⊃Column, Column⊃위젯, Form⊃Field).

- [ ] **Step 1: 실패 테스트 작성 (설정 빌더 계약)**

`grapesConfig.test.ts`:

```ts
import { describe, it, expect } from 'vitest'
import { BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildEditorConfig, buildTraitDefs } from '../grapesConfig'

describe('GrapesJS 디자이너 설정(잠금)', () => {
  it('8개 블록만 노출(§5 컴포넌트 세트)', () => {
    expect(BLOCK_DEFS.map(b => b.id).sort()).toEqual(
      ['nx-button', 'nx-column', 'nx-field', 'nx-form', 'nx-grid', 'nx-row', 'nx-section', 'nx-text'].sort())
  })

  it('8개 컴포넌트 type 정의', () => {
    expect(COMPONENT_TYPE_DEFS.map(c => c.type).sort()).toEqual(
      ['nx-button', 'nx-column', 'nx-field', 'nx-form', 'nx-grid', 'nx-row', 'nx-section', 'nx-text'].sort())
  })

  it('init 설정은 RTE·스타일·기본 블록을 잠근다', () => {
    const cfg = buildEditorConfig(document.createElement('div'))
    expect(cfg.storageManager).toBe(false)        // 저장은 수동(api.ts)
    expect(cfg.rte).toBe(false)                    // 리치텍스트 비노출(XSS 표면 제거)
    expect(cfg.blockManager?.blocks).toEqual([])   // 기본 블록 없음(우리가 등록)
    expect(cfg.styleManager?.sectors).toEqual([])  // 스타일 섹터 없음
  })

  it('중첩 규칙: column은 위젯을 droppable, section은 row만', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const section = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-section')!
    expect(col.model.droppable).toContain('nx-grid')
    expect(section.model.droppable).toBe('nx-row')
  })

  it('buildTraitDefs는 grid에 read 쿼리 드롭다운, button에 write 쿼리 드롭다운', () => {
    const traits = buildTraitDefs({ reads: ['MDM.PlantList'], writes: [{ id: 'MDM.CreatePlant', requiredPermission: 'mdm:manage' }] })
    const gridQuery = traits['nx-grid'].find(t => t.name === 'data-query-id')!
    expect(gridQuery.options!.map(o => o.id)).toContain('MDM.PlantList')
    const btnCmd = traits['nx-button'].find(t => t.name === 'data-command')!
    expect(btnCmd.options!.map(o => o.id)).toContain('MDM.CreatePlant')
  })
})
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/grapesConfig.test.ts`
Expected: FAIL — 모듈/심볼 미존재.

- [ ] **Step 3: grapesConfig.ts 구현**

```ts
import type { GrapesNode } from './layout'

// GrapesJS init 옵션의 최소 타입(우리가 검증/사용하는 부분만). grapesjs 타입과 호환되는 부분집합.
export interface EditorConfig {
  container: HTMLElement
  height: string
  fromElement: boolean
  storageManager: false
  rte: false
  blockManager: { appendTo?: string; blocks: unknown[] }
  styleManager: { sectors: unknown[] }
  panels: { defaults: unknown[] }
}

export interface BlockDef { id: string; label: string; content: GrapesNode }
export interface TraitOption { id: string; name: string }
export interface TraitDef { type: string; name: string; label: string; options?: TraitOption[] }
export interface ComponentTypeDef {
  type: string
  model: { defaults: { name: string; draggable: boolean | string; droppable: boolean | string } & Record<string, unknown>; droppable: boolean | string }
}

// 잠금: 저장 수동(api.ts), RTE/스타일/기본 블록 비노출 → 산출 트리에 GrapesJS 사설 마크업/스타일이 새지 않음.
export function buildEditorConfig(container: HTMLElement): EditorConfig {
  return {
    container,
    height: '100%',
    fromElement: false,
    storageManager: false,
    rte: false,
    blockManager: { blocks: [] },   // 기본 블록 제거 — registerBlocks가 8개만 추가
    styleManager: { sectors: [] },  // 스타일 매니저 비노출
    panels: { defaults: [] },       // 기본 패널 트림
  }
}

// 8개 컴포넌트 type — kind 1:1. 중첩 규칙으로 잘못된 조합을 캔버스에서 막는다.
export const COMPONENT_TYPE_DEFS: ComponentTypeDef[] = [
  { type: 'nx-section', model: { defaults: { name: 'Section', draggable: true, droppable: 'nx-row' }, droppable: 'nx-row' } },
  { type: 'nx-row', model: { defaults: { name: 'Row', draggable: 'nx-section', droppable: 'nx-column' }, droppable: 'nx-column' } },
  { type: 'nx-column', model: { defaults: { name: 'Column', draggable: 'nx-row', droppable: 'nx-grid,nx-form,nx-button,nx-text' }, droppable: 'nx-grid,nx-form,nx-button,nx-text' } },
  { type: 'nx-grid', model: { defaults: { name: 'DataGrid', draggable: 'nx-column', droppable: false }, droppable: false } },
  { type: 'nx-form', model: { defaults: { name: 'Form', draggable: 'nx-column', droppable: 'nx-field' }, droppable: 'nx-field' } },
  { type: 'nx-field', model: { defaults: { name: 'Field', draggable: 'nx-form', droppable: false }, droppable: false } },
  { type: 'nx-button', model: { defaults: { name: 'CommandButton', draggable: 'nx-column', droppable: false }, droppable: false } },
  { type: 'nx-text', model: { defaults: { name: 'Text', draggable: 'nx-column', droppable: false }, droppable: false } },
]

// 블록 팔레트 — §5 8개만. content는 layoutToComponent와 같은 attributes 키 규약을 따른다.
export const BLOCK_DEFS: BlockDef[] = [
  { id: 'nx-section', label: '섹션', content: { type: 'nx-section', attributes: {}, components: [] } },
  { id: 'nx-row', label: '행', content: { type: 'nx-row', attributes: {}, components: [] } },
  { id: 'nx-column', label: '열', content: { type: 'nx-column', attributes: { 'data-span': 6 }, components: [] } },
  { id: 'nx-grid', label: '데이터 그리드', content: { type: 'nx-grid', attributes: {} } },
  { id: 'nx-form', label: '폼', content: { type: 'nx-form', attributes: {}, components: [] } },
  { id: 'nx-field', label: '필드', content: { type: 'nx-field', attributes: { 'data-field-key': '' } } },
  { id: 'nx-button', label: '명령 버튼', content: { type: 'nx-button', attributes: { 'data-label': '버튼' } } },
  { id: 'nx-text', label: '텍스트', content: { type: 'nx-text', attributes: { 'data-text': '텍스트' } } },
]

export interface QueryCatalog { reads: string[]; writes: { id: string; requiredPermission: string | null }[] }

// 트레이트 정의 — 바인딩 키만 노출(raw HTML/CSS/style 트레이트 없음). 드롭다운은 카탈로그 소스.
export function buildTraitDefs(queries: QueryCatalog): Record<string, TraitDef[]> {
  const readOpts = queries.reads.map(id => ({ id, name: id }))
  const writeOpts = queries.writes.map(w => ({ id: w.id, name: w.id }))
  return {
    'nx-section': [{ type: 'text', name: 'data-title', label: '제목' }],
    'nx-row': [],
    'nx-column': [{ type: 'number', name: 'data-span', label: '폭(1-12)' }],
    'nx-grid': [{ type: 'select', name: 'data-query-id', label: '조회 쿼리', options: readOpts }],
    'nx-form': [{ type: 'select', name: 'data-save-query-id', label: '저장 쿼리', options: writeOpts }],
    'nx-field': [
      { type: 'text', name: 'data-field-key', label: '필드 키' },
      { type: 'text', name: 'data-label', label: '라벨' },
    ],
    'nx-button': [
      { type: 'text', name: 'data-label', label: '라벨' },
      { type: 'select', name: 'data-command', label: '명령 쿼리', options: writeOpts },
    ],
    'nx-text': [{ type: 'text', name: 'data-text', label: '텍스트' }],
  }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/grapesConfig.test.ts`
Expected: 모든 테스트 PASS.

- [ ] **Step 5: ScreenEditor.tsx 글루 작성 (수동 검증 대상, 단위 테스트 비대상)**

```tsx
import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import grapesjs, { type Editor } from 'grapesjs'
import 'grapesjs/dist/css/grapes.min.css'
import { getAccessToken } from '../api/client'
import { hasPermission } from '../auth/jwt'
import { loadDefinition, saveDefinition, listQueries } from '../designer/api'
import { layoutToComponent, componentToLayout } from '../designer/mapping'
import {
  buildEditorConfig, BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildTraitDefs, type QueryCatalog,
} from '../designer/grapesConfig'
import type { GrapesNode, LayoutNode } from '../designer/layout'

// 캔버스 컴포넌트 트리(wrapper의 자식들)를 GrapesNode[]로 직렬화 → 첫 루트를 LayoutNode로.
function readRootLayout(editor: Editor): LayoutNode | null {
  const roots = editor.getComponents().map(c => c.toJSON() as GrapesNode)
  for (const r of roots) {
    const node = componentToLayout(r)
    if (node) return node
  }
  return null
}

export function ScreenEditor() {
  const { uiId } = useParams<{ uiId: string }>()
  const hostRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<Editor | null>(null)
  const [title, setTitle] = useState('')
  const [status, setStatus] = useState('초기화 중…')

  const canManage = hasPermission(getAccessToken(), 'sys:manage')

  useEffect(() => {
    if (!hostRef.current || !canManage) return
    let disposed = false
    const editor = grapesjs.init(buildEditorConfig(hostRef.current) as never)
    editorRef.current = editor

    // 8개 컴포넌트 type 등록 + 트레이트 바인딩(카탈로그 드롭다운).
    listQueries()
      .then((cat: QueryCatalog) => {
        const traits = buildTraitDefs(cat)
        for (const c of COMPONENT_TYPE_DEFS) {
          editor.DomComponents.addType(c.type, {
            model: { defaults: { ...c.model.defaults, traits: traits[c.type] ?? [] } },
          })
        }
        for (const b of BLOCK_DEFS) editor.BlockManager.add(b.id, { label: b.label, content: b.content })
        return uiId ? loadDefinition(uiId) : Promise.resolve({ title: '', layout: null as LayoutNode | null })
      })
      .then(({ title: loaded, layout }) => {
        if (disposed) return
        setTitle(loaded || (uiId ?? ''))
        editor.setComponents(layout ? [layoutToComponent(layout)] : [{ type: 'nx-section', attributes: {}, components: [] }])
        setStatus('준비됨')
      })
      .catch(() => { if (!disposed) setStatus('로드 실패(권한/네트워크 확인)') })

    return () => { disposed = true; editor.destroy(); editorRef.current = null }
  }, [uiId, canManage])

  async function handleSave() {
    const editor = editorRef.current
    if (!editor || !uiId) return
    try {
      setStatus('저장 중…')
      await saveDefinition(uiId, title || uiId, readRootLayout(editor))
      setStatus('저장됨')
    } catch {
      setStatus('저장 실패(권한 sys:manage 확인)')
    }
  }

  if (!canManage) return <div style={{ padding: '2rem' }}>화면 디자이너 권한(sys:manage)이 없습니다.</div>

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh' }}>
      <header style={{ display: 'flex', gap: 8, alignItems: 'center', padding: 8, borderBottom: '1px solid #ddd' }}>
        <strong>화면 디자이너</strong>
        <input aria-label="화면 제목" value={title} onChange={e => setTitle(e.target.value)} placeholder="화면 제목" />
        <span>UI ID: {uiId ?? '(미지정)'}</span>
        <button onClick={handleSave} disabled={!uiId}>저장</button>
        <span style={{ marginLeft: 'auto' }}>{status}</span>
      </header>
      <div ref={hostRef} style={{ flex: 1, minHeight: 0 }} />
    </div>
  )
}
```

- [ ] **Step 6: 타입 빌드 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx tsc -b`
Expected: 0 errors.
주의: grapesjs는 자체 타입을 제공한다. `grapesjs.init(...)` 옵션 타입이 우리 `EditorConfig`와 완전 호환되지 않으면 `as never`로 캐스팅(이미 적용). `addType`/`BlockManager.add`/`setComponents` 시그니처는 설치된 grapesjs(^0.21) 기준으로 맞추고, 어긋나면 설치 버전 API에 맞춰 조정하라. CSS import(`grapesjs/dist/css/grapes.min.css`)는 vite가 처리.

- [ ] **Step 7: 전체 vitest 통과 확인**

Run: `cd src/01.Web/NexaOne.Spa && npm test`
Expected: mapping/api/grapesConfig/smoke 전부 PASS.

- [ ] **Step 8: 커밋**

커밋 메시지: `feat(spa): GrapesJS 디자이너 — 잠금 설정·8블록·트레이트(설정 테스트) + ScreenEditor 글루`

---

## Task 5: 라우터 통합 + 디자이너 진입

**Files:**
- Modify: `src/01.Web/NexaOne.Spa/src/main.tsx`
- Modify: `src/01.Web/NexaOne.Spa/src/App.tsx`
- Modify: `src/01.Web/NexaOne.Spa/src/features/Dashboard.tsx`
- Test: `src/01.Web/NexaOne.Spa/src/designer/__tests__/routing.test.tsx`

**스코프 메모:** 기존 상태 기반 세션은 유지하되 `react-router-dom`으로 감싼다. 호스트는 `/spa/*`를 `index.html`로 폴백(MapFallbackToFile)하므로 `BrowserRouter basename="/spa"`. 세션은 메모리 전역(새로고침 시 소실) → 세션 없으면 디자이너 라우트는 로그인으로 리다이렉트.

- [ ] **Step 1: 실패 테스트 작성 (라우팅 가드)**

`routing.test.tsx`:

```tsx
import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { AppRoutes } from '../../App'

// 세션 없이 /designer 진입 → 로그인 화면(리다이렉트). 세션 주입은 별도 흐름이라 여기선 미인증 가드만.
describe('SPA 라우팅', () => {
  it('미인증 상태에서 /designer는 로그인으로 폴백', () => {
    render(
      <MemoryRouter initialEntries={['/designer/DEMO']}>
        <AppRoutes session={null} setSession={() => {}} />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: /Pro-Code/i })).toBeInTheDocument()  // Login의 h1
  })

  it('루트 경로는 로그인 화면', () => {
    render(
      <MemoryRouter initialEntries={['/']}>
        <AppRoutes session={null} setSession={() => {}} />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: /Pro-Code/i })).toBeInTheDocument()
  })
})
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/routing.test.tsx`
Expected: FAIL — `AppRoutes` export 없음.

- [ ] **Step 3: App.tsx에 라우트 도입 (AppRoutes export)**

```tsx
import { useState } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { Login } from './features/Login'
import { Dashboard } from './features/Dashboard'
import { ScreenEditor } from './features/ScreenEditor'
import { setSession as setClientSession } from './api/client'
import type { LoginResponse } from './api/auth'

// 라우트 트리(테스트 가능하도록 분리) — 세션은 상위에서 주입. 미인증 디자이너 접근은 로그인으로 폴백.
export function AppRoutes({ session, setSession }: {
  session: LoginResponse | null
  setSession: (s: LoginResponse | null) => void
}) {
  return (
    <Routes>
      <Route path="/" element={
        session
          ? <Dashboard session={session} onLogout={() => setSession(null)} />
          : <Login onLoggedIn={setSession} />
      } />
      <Route path="/designer/:uiId" element={session ? <ScreenEditor /> : <Navigate to="/" replace />} />
      <Route path="/designer" element={session ? <ScreenEditor /> : <Navigate to="/" replace />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

// 공존 데모: 동일 NexaOne.API(REST/SignalR/JWT) 위에서 동작하는 React Pro-Code SPA.
export function App() {
  const [session, setSession] = useState<LoginResponse | null>(null)
  // 로그인 성공 시 client 모듈 세션도 동기화(apiFetch Bearer 토큰 소스). 로그아웃 시 해제.
  const sync = (s: LoginResponse | null) => {
    if (s) setClientSession({ accessToken: s.accessToken, refreshToken: s.refreshToken, userId: s.userId })
    else setClientSession(null)
    setSession(s)
  }
  return <AppRoutes session={session} setSession={sync} />
}
```

주의: 기존 `login()`(auth.ts)이 이미 `setSession`을 호출하므로 client 세션은 로그인 시 설정된다. 위 `sync`의 `setClientSession`은 방어적 동기화(로그아웃 시 해제 포함) — 중복이지만 무해. `Login`은 `onLoggedIn`에 `LoginResponse`를 넘긴다(기존 시그니처 유지).

- [ ] **Step 4: main.tsx에 BrowserRouter 래핑**

```tsx
import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { App } from './App'
import { ErrorBoundary } from './components/ErrorBoundary'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ErrorBoundary>
      <BrowserRouter basename="/spa">
        <App />
      </BrowserRouter>
    </ErrorBoundary>
  </React.StrictMode>,
)
```

- [ ] **Step 5: Dashboard에 디자이너 진입 링크 추가 (sys:manage 게이트)**

[Dashboard.tsx](../../../src/01.Web/NexaOne.Spa/src/features/Dashboard.tsx)에 `Link` import와 권한 게이트 링크를 추가한다. import 라인 수정:

```tsx
import { Link } from 'react-router-dom'
```

`canControl` 계산 다음에 디자이너 권한 추가:

```tsx
  const canControl = hasPermission(getAccessToken(), 'fdc:control')
  const canDesign = hasPermission(getAccessToken(), 'sys:manage')
```

`<header>` 블록의 로그아웃 버튼 앞에 링크 삽입(헤더 `<span>` 다음, `<button onClick={handleLogout}>` 앞):

```tsx
        <span>{session.userName} ({session.roles.join(', ')}) @ {session.plantId}</span>
        <span style={{ display: 'flex', gap: 8 }}>
          {canDesign && <Link to="/designer/DEMO_DESIGNER">화면 디자이너</Link>}
          <button onClick={handleLogout}>로그아웃</button>
        </span>
```

(기존 `<button onClick={handleLogout}>로그아웃</button>` 단독 라인을 위 `<span>` 묶음으로 교체)

- [ ] **Step 6: 라우팅 테스트 통과 확인**

Run: `cd src/01.Web/NexaOne.Spa && npx vitest run src/designer/__tests__/routing.test.tsx`
Expected: 2 passed.
주의: 테스트는 `ScreenEditor`를 렌더하지 않는다(미인증 → Login). GrapesJS는 마운트되지 않으므로 jsdom에서 안전.

- [ ] **Step 7: 전체 검증 (vitest + tsc)**

Run: `cd src/01.Web/NexaOne.Spa && npm test && npx tsc -b`
Expected: 전체 vitest PASS, tsc 0 errors.

- [ ] **Step 8: 커밋**

커밋 메시지: `feat(spa): react-router 통합(/spa/designer/:uiId) + Dashboard 디자이너 진입(sys:manage 게이트)`

---

## Task 6 (컨트롤러 직접 수행): 빌드·회귀·수동 캔버스 검증

서브에이전트가 아니라 컨트롤러가 직접 수행한다(빌드/회귀 게이트 + 수동 검증 문서화).

- [ ] **Step 1: 프로덕션 빌드 (TS 타입체크 + 번들)**

Run: `cd src/01.Web/NexaOne.Spa && npm run build`
Expected: `tsc -b` 0 errors → `vite build` 성공 → `../../00.Main/NexaOne.Server/wwwroot/spa`에 출력(gitignored, 커밋 불필요). grapesjs 번들 포함 확인(자산 크기 증가 정상).

- [ ] **Step 2: 전체 SPA 단위 테스트**

Run: `cd src/01.Web/NexaOne.Spa && npm test`
Expected: mapping·api·grapesConfig·routing·smoke 전부 PASS.

- [ ] **Step 3: .NET 회귀 (SPA는 C# 무영향 — 게이트웨이/호스트 테스트 불변 확인)**

Run: `dotnet build NexaOne.sln -c Debug` (또는 NexaOne.Server + ServerTests만)
Run: `dotnet test test/NexaOne.ServerTests/NexaOne.ServerTests.csproj`
Expected: 빌드 0 errors, ServerTests 그린(SpaStaticServingTests는 자체 더미 index.html로 npm 빌드 비의존 — 불변).

- [ ] **Step 4: 수동 캔버스 검증 (문서화 — 자동화 한계 명시)**

GrapesJS 캔버스 드래그&드롭·트레이트·잠금은 jsdom 단위 테스트로 신뢰 불가하므로 수동 절차를 기록한다(가능 시 실행, 불가 시 절차만 명시하고 자동 게이트=빌드+vitest+.NET로 충분함을 보고):
  1. `npm run dev`(5173) + 호스트(`dotnet run --project src/00.Main/NexaOne.Server`, 게이트웨이 모드) 기동, 로그인(sys:manage 보유 계정).
  2. `/spa/designer/SMOKE_TEST` 진입 → 빈 Section 캔버스 + 8블록 팔레트 확인. RTE/스타일 패널 부재 확인(잠금).
  3. Section→Row→Column→Grid 배치, Grid 트레이트에서 조회 쿼리 드롭다운(MDM.PlantList) 선택. 저장.
  4. 새로고침/재진입 → 저장한 트리가 동일 복원(라운드트립). DB(SYS_SCREEN_DEFINITION) 행 생성 확인.
  5. `/meta/SMOKE_TEST`(Blazor 런타임) → 디자인한 화면이 LayoutRenderer로 렌더되는지 확인(생산자→소비자 e2e).

- [ ] **Step 5: 최종 통합 리뷰 + ff-merge**

전체 변경에 대해 홀리스틱 코드 리뷰(서브에이전트) 후, 통과 시 `superpowers:finishing-a-development-branch`로 main에 ff-merge. sln/lockfile 아티팩트 가드: `git checkout main` 시 NexaOne.sln 더티면 `git checkout -- NexaOne.sln`. push는 사용자 미요청 → 안 함.

---

## Self-Review (계획 검토)

**1. Spec coverage(§5~§12 대조):**
- §5 레이아웃 스키마 8노드 → Task 2 `layout.ts`/`mapping.ts`(라운드트립 골든=§5 예시). ✓
- §8 디자이너(잠금·StorageManager 매핑·드롭다운·단일 진리원천) → Task 4 `grapesConfig.ts`(잠금/블록/트레이트) + Task 2 매핑(StorageManager 역할은 명시적 Save/Load+순수 매핑으로 대체, 메모로 명문화). ✓
- §9 쿼리 메타데이터 엔드포인트 → 이미 존재(Phase 5a `/api/v1/sys/queries`), Task 3 `listQueries`가 소비. ✓
- §7 보안(권한 게이트·XSS) → 서버 sys:manage(이미 강제) + 디자이너 진입 권한 게이트(Task 5) + RTE/style 잠금(Task 4)으로 마크업 비유입. ✓
- §12 테스트 전략(StorageManager 매핑 단위·잠금·드롭다운 소스 / 캔버스 수동) → Task 2/3/4 vitest + Task 6 수동. ✓
- §10 하위호환 → 디자이너는 병행 추가, 백엔드 무변경, layout 우선 정의(평면 fields=[]). ✓

**2. Placeholder scan:** 코드 스텝은 전부 실제 코드. GrapesJS 글루(`ScreenEditor.tsx`)·init 호출은 설치 버전 API 의존 부분을 메모로 명시(추상 "TBD" 없음). ✓

**3. Type consistency:** `LayoutNode`/`GrapesNode`/`ScreenDefinitionDto`(layout.ts) ↔ mapping.ts ↔ api.ts ↔ grapesConfig.ts ↔ ScreenEditor.tsx에서 동일 심볼·키('data-*' 규약). `buildDefinitionJson`/`parseDefinition`/`layoutToComponent`/`componentToLayout`/`loadDefinition`/`saveDefinition`/`listQueries`/`buildEditorConfig`/`BLOCK_DEFS`/`COMPONENT_TYPE_DEFS`/`buildTraitDefs`/`AppRoutes` — 정의처와 사용처 시그니처 일치. ✓

**4. 알려진 한계(명시):** GrapesJS 캔버스/init은 jsdom 단위 테스트 비대상 → 설정 빌더(순수)만 테스트, 캔버스는 수동(§12와 일치). `ScreenEditor.tsx`의 grapesjs API 호출은 설치된 ^0.21 시그니처에 맞춰 구현자가 조정(타입 빌드가 게이트).
