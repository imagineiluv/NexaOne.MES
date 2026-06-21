import type { GrapesNode } from './layout'

// GrapesJS init 옵션의 최소 타입(우리가 검증/사용하는 부분만). 잠금: storageManager=false(저장 수동),
// styleManager.sectors=[]·기본 블록=[]·panels.defaults=[](스타일/코드/RTE chrome 비노출).
// 블록·트레이트 매니저는 패널 버튼 대신 전용 컨테이너(appendTo)에 직접 마운트한다 — 이렇게 해야
// 의도한 8블록 팔레트와 트레이트 편집기는 노출되면서 스타일/코드/RTE UI는 잠긴다(GrapesJS 커스텀 UI 표준 패턴).
// RTE는 별도 키가 아니라 text-type 컴포넌트 부재로 잠긴다(어떤 nx- type도 text를 확장하거나 editable을 켜지 않음).
export interface EditorConfig {
  container: HTMLElement
  height: string
  fromElement: boolean
  storageManager: false
  blockManager: { appendTo?: HTMLElement; blocks: unknown[] }
  traitManager: { appendTo?: HTMLElement }
  styleManager: { sectors: unknown[] }
  panels: { defaults: unknown[] }
}

export function buildEditorConfig(container: HTMLElement, blocksEl?: HTMLElement, traitsEl?: HTMLElement): EditorConfig {
  return {
    container,
    height: '100%',
    fromElement: false,
    storageManager: false,
    blockManager: { blocks: [], ...(blocksEl ? { appendTo: blocksEl } : {}) },
    traitManager: { ...(traitsEl ? { appendTo: traitsEl } : {}) },
    styleManager: { sectors: [] },
    panels: { defaults: [] },
  }
}

export interface BlockDef { id: string; label: string; content: GrapesNode }
export interface TraitOption { id: string; name: string }
export interface TraitDef { type: string; name: string; label: string; options?: TraitOption[] }

// 컴포넌트 type의 선언적 중첩 규칙. allowedChildren=자식으로 허용할 type(droppable 대상),
// allowedParents=부모로 허용할 type(draggable 대상). 빈 allowedChildren=leaf(droppable false),
// 빈 allowedParents=최상위(draggable true).
export interface ComponentTypeDef {
  type: string
  name: string
  allowedChildren: string[]
  allowedParents: string[]
}

export const COMPONENT_TYPE_DEFS: ComponentTypeDef[] = [
  { type: 'nx-section', name: 'Section', allowedChildren: ['nx-row'], allowedParents: [] },
  { type: 'nx-row', name: 'Row', allowedChildren: ['nx-column'], allowedParents: ['nx-section'] },
  { type: 'nx-column', name: 'Column', allowedChildren: ['nx-grid', 'nx-form', 'nx-button', 'nx-text'], allowedParents: ['nx-row'] },
  { type: 'nx-grid', name: 'DataGrid', allowedChildren: [], allowedParents: ['nx-column'] },
  { type: 'nx-form', name: 'Form', allowedChildren: ['nx-field'], allowedParents: ['nx-column'] },
  { type: 'nx-field', name: 'Field', allowedChildren: [], allowedParents: ['nx-form'] },
  { type: 'nx-button', name: 'CommandButton', allowedChildren: [], allowedParents: ['nx-column'] },
  { type: 'nx-text', name: 'Text', allowedChildren: [], allowedParents: ['nx-column'] },
]

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

// GrapesJS 컴포넌트의 최소 형태(type 기반 비교) — droppable/draggable 함수가 받는 인자.
export interface TypeMatchable { is(type: string): boolean }

// 선언적 중첩 규칙을 GrapesJS model.defaults로 변환. droppable/draggable은 반드시 함수여야 한다 —
// GrapesJS는 문자열 규칙을 CSS 셀렉터(el.matches)로 평가하므로 tagName 미지정(div) 컴포넌트엔 항상 false가 되어
// 드롭이 전면 거부된다. 따라서 type 기반 규칙(src.is(type))을 함수로 준다.
export function toModelDefaults(def: ComponentTypeDef, traits: TraitDef[]): Record<string, unknown> {
  const droppable = def.allowedChildren.length === 0
    ? false
    : (src: TypeMatchable) => def.allowedChildren.some(t => src.is(t))
  const draggable = def.allowedParents.length === 0
    ? true
    : (_src: TypeMatchable, trg: TypeMatchable) => !!trg && def.allowedParents.some(t => trg.is(t))
  return { name: def.name, droppable, draggable, traits }
}

export interface QueryCatalog { reads: string[]; writes: { id: string; requiredPermission: string | null }[] }

// FieldType(5종) 셀렉트 옵션 — layout.ts FieldType과 정확히 일치해야 한다(C# FieldType 미러).
const FIELD_TYPE_OPTS: TraitOption[] = ['Text', 'Number', 'Boolean', 'Date', 'Select'].map(t => ({ id: t, name: t }))

export function buildTraitDefs(queries: QueryCatalog): Record<string, TraitDef[]> {
  const readOpts = queries.reads.map(id => ({ id, name: id }))
  const writeOpts = queries.writes.map(w => ({ id: w.id, name: w.id }))
  return {
    'nx-section': [{ type: 'text', name: 'data-title', label: '제목' }],
    'nx-row': [],
    'nx-column': [{ type: 'number', name: 'data-span', label: '폭(1-12)' }],
    'nx-grid': [
      { type: 'select', name: 'data-query-id', label: '조회 쿼리', options: readOpts },
      // 컬럼 작성 트레이트. JSON 인코딩 — key/caption에 콤마·콜론이 있어도 무손실 라운드트립.
      { type: 'text', name: 'data-columns', label: '컬럼(JSON: [{key,caption,visible}])' },
    ],
    'nx-form': [{ type: 'select', name: 'data-save-query-id', label: '저장 쿼리', options: writeOpts }],
    // FieldDefinition 전체를 이산 트레이트로 노출(data-field JSON blob 폐기, 트레이트 패널이 단일 편집 출처).
    'nx-field': [
      { type: 'text', name: 'data-field-key', label: '필드 키' },
      { type: 'text', name: 'data-field-label', label: '라벨' },
      { type: 'select', name: 'data-field-type', label: '타입', options: FIELD_TYPE_OPTS },
      { type: 'checkbox', name: 'data-field-required', label: '필수' },
      { type: 'checkbox', name: 'data-field-readonly', label: '읽기전용' },
      { type: 'text', name: 'data-field-options', label: '옵션(JSON 배열, Select용)' },
    ],
    'nx-button': [
      { type: 'text', name: 'data-label', label: '라벨' },
      { type: 'select', name: 'data-command', label: '명령 쿼리', options: writeOpts },
    ],
    'nx-text': [{ type: 'text', name: 'data-text', label: '텍스트' }],
  }
}
