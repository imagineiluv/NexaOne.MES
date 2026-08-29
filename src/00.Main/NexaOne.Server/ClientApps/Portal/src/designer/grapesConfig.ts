import type { GrapesNode, QueryCatalog, QueryDescriptor } from './layout'
import designTokensCss from '../../../../../../../tokens.css?raw'
export type { QueryCatalog } from './layout'

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
  noticeOnUnload: boolean
  showOffsetsSelected: boolean
  canvas: { frameStyle: string }
  blockManager: { appendTo?: HTMLElement; blocks: unknown[] }
  traitManager: { appendTo?: HTMLElement }
  styleManager: { sectors: unknown[] }
  panels: { defaults: unknown[] }
}

// GrapesJS 캔버스는 iframe이므로 Portal 스타일을 상속하지 않는다. 런타임 위젯을 흉내 내는 편집 전용
// 프리뷰 토큰을 frameStyle로 주입해 빈 div 대신 구조·드롭 영역·위젯 종류가 즉시 보이게 한다.
// data-nx-component는 저장 스키마에 포함되지 않는 편집 표식이며 mapping 단계에서 매번 재생성된다.
export const DESIGNER_CANVAS_STYLE = `${designTokensCss}
  * { box-sizing: border-box; }
  html, body { overflow-x: clip; }
  html { min-height: 100%; background: var(--nx-bg); color-scheme: light; }
  html[data-theme="dark"] { color-scheme: dark; }
  body {
    min-height: 100dvh; margin: 0; padding: var(--space-lg); color: var(--nx-text); background: var(--nx-bg);
    font-family: var(--font-body);
  }
  [data-nx-component] {
    position: relative;
    transition: border-color var(--dur-micro) var(--ease-out),
                box-shadow var(--dur-micro) var(--ease-out),
                background-color var(--dur-micro) var(--ease-out);
  }
  [data-nx-component="nx-section"] {
    min-height: calc(100vh - 48px); padding: var(--space-lg); border: 1px solid var(--nx-border); border-radius: var(--radius-panel);
    background: var(--nx-card); box-shadow: var(--nx-shadow-sm);
  }
  [data-nx-component="nx-section"]::before {
    content: attr(data-title); display: block; min-height: 18px; margin: 0 0 var(--space-md); color: var(--nx-text);
    font-size: 18px; font-weight: 750;
  }
  [data-nx-component="nx-section"]:empty::after {
    content: "행 블록을 이곳으로 드래그하거나 키보드로 추가하세요"; display: grid; place-items: center;
    min-height: calc(100vh - 140px); padding: calc(var(--space-lg) + var(--space-2xs)); border: 2px dashed var(--nx-border); border-radius: var(--radius-card);
    color: var(--nx-muted); background: var(--nx-head); font-size: 14px; text-align: center;
  }
  [data-nx-component="nx-row"] {
    display: grid; grid-template-columns: repeat(12, minmax(0, 1fr)); gap: var(--space-sm); min-height: 92px;
    margin: 0 0 var(--space-sm); padding: var(--space-sm); border: 1px dashed var(--nx-border); border-radius: var(--radius-card);
    background: var(--nx-head);
  }
  [data-nx-component="nx-row"]:empty::after {
    content: "열 블록을 추가하세요"; grid-column: 1 / -1; display: grid; place-items: center; color: var(--nx-muted);
  }
  [data-nx-component="nx-column"] {
    grid-column: span 12; display: grid; align-content: start; gap: var(--space-xs); min-height: 72px; padding: var(--space-sm);
    border: 1px dashed var(--nx-border); border-radius: var(--radius-card); background: var(--nx-card);
  }
  [data-nx-component="nx-column"]:empty::after { content: "위젯을 이 열에 놓으세요"; color: var(--nx-muted); text-align: center; padding: var(--space-md) var(--space-xs); }
  [data-nx-component="nx-column"][data-span="1"] { grid-column: span 1; }
  [data-nx-component="nx-column"][data-span="2"] { grid-column: span 2; }
  [data-nx-component="nx-column"][data-span="3"] { grid-column: span 3; }
  [data-nx-component="nx-column"][data-span="4"] { grid-column: span 4; }
  [data-nx-component="nx-column"][data-span="5"] { grid-column: span 5; }
  [data-nx-component="nx-column"][data-span="6"] { grid-column: span 6; }
  [data-nx-component="nx-column"][data-span="7"] { grid-column: span 7; }
  [data-nx-component="nx-column"][data-span="8"] { grid-column: span 8; }
  [data-nx-component="nx-column"][data-span="9"] { grid-column: span 9; }
  [data-nx-component="nx-column"][data-span="10"] { grid-column: span 10; }
  [data-nx-component="nx-column"][data-span="11"] { grid-column: span 11; }
  [data-nx-component="nx-column"][data-span="12"] { grid-column: span 12; }
  [data-nx-component="nx-grid"], [data-nx-component="nx-form"], [data-nx-component="nx-collection"],
  [data-nx-component="nx-kpi"], [data-nx-component="nx-badge-widget"], [data-nx-component="nx-trend-chart"] {
    min-height: 78px; padding: var(--space-md); border: 1px solid var(--nx-border); border-radius: var(--radius-card); background: var(--nx-card);
    box-shadow: var(--nx-shadow-sm);
  }
  [data-nx-component="nx-grid"] { min-height: 190px; background: linear-gradient(var(--nx-head) 36px, transparent 36px), var(--nx-card); }
  [data-nx-component="nx-grid"]::before { content: "데이터 그리드  ·  " attr(data-query-id); font-weight: 700; }
  [data-nx-component="nx-grid"]::after {
    content: ""; position: absolute; inset: calc(var(--space-3xl) - var(--space-sm)) var(--space-md) var(--space-md); opacity: .55;
    background: repeating-linear-gradient(to bottom, var(--nx-border) 0 1px, transparent 1px 30px);
  }
  html[data-nx-manage-preview="standard"] [data-nx-component="nx-grid"] {
    min-height: 190px;
  }
  html[data-nx-manage-preview="dense"] [data-nx-component="nx-grid"] {
    min-height: 220px;
  }
  html[data-nx-manage-preview="dense"] [data-nx-component="nx-grid"]::after {
    inset: calc(var(--space-xl) + var(--space-md)) var(--space-md) var(--space-md);
    background: repeating-linear-gradient(to bottom, var(--nx-border) 0 1px, transparent 1px 22px);
  }
  html[data-nx-manage-preview="cards"] [data-nx-component="nx-grid"] {
    min-height: 230px;
    background: var(--nx-head);
  }
  html[data-nx-manage-preview="cards"] [data-nx-component="nx-grid"]::before {
    content: "카드 목록 ·  " attr(data-query-id);
  }
  html[data-nx-manage-preview="cards"] [data-nx-component="nx-grid"]::after {
    inset: calc(var(--space-3xl) - var(--space-sm)) var(--space-md) var(--space-md); opacity: 1; border-radius: var(--radius-card);
    background:
      linear-gradient(90deg, var(--nx-teal) 0 4px, transparent 4px),
      repeating-linear-gradient(to bottom, var(--nx-card) 0 40px, transparent 40px 48px);
    box-shadow: inset 0 0 0 1px var(--nx-border);
  }
  html[data-nx-manage-preview="split"] [data-nx-component="nx-grid"] {
    min-height: 250px;
    background: var(--nx-card);
  }
  html[data-nx-manage-preview="split"] [data-nx-component="nx-grid"]::before {
    content: "목록 + 선택 상세 ·  " attr(data-query-id);
  }
  html[data-nx-manage-preview="split"] [data-nx-component="nx-grid"]::after {
    inset: calc(var(--space-3xl) - var(--space-sm)) var(--space-md) var(--space-md); opacity: .72; border: 1px solid var(--nx-border); border-radius: var(--radius-card);
    background:
      repeating-linear-gradient(to bottom, var(--nx-border) 0 1px, transparent 1px 28px) left / 56% 100% no-repeat,
      linear-gradient(var(--nx-head), var(--nx-head)) right / 42% 100% no-repeat;
  }
  [data-nx-component="nx-form"]::before { content: "입력 폼  ·  " attr(data-save-query-id); display: block; margin-bottom: var(--space-sm); font-weight: 700; }
  [data-nx-component="nx-form"]:empty::after { content: "필드 블록을 추가하세요"; color: var(--nx-muted); }
  [data-nx-component="nx-collection"]::before { content: attr(data-label) "  ·  반복 항목"; display: block; margin-bottom: var(--space-sm); font-weight: 700; }
  [data-nx-component="nx-collection"]:empty::after { content: "반복할 필드 블록을 추가하세요"; color: var(--nx-muted); }
  [data-nx-component="nx-field"] {
    min-height: 52px; padding: var(--space-xs) var(--space-sm); border: 1px solid var(--nx-border); border-radius: var(--radius-card); background: var(--nx-head);
  }
  [data-nx-component="nx-field"]::before { content: attr(data-field-label); display: block; margin-bottom: var(--space-2xs); font-weight: 700; }
  [data-nx-component="nx-field"]::after { content: attr(data-field-key) "  ·  필드"; color: var(--nx-muted); font-size: 12px; }
  [data-nx-component="nx-button"] {
    display: inline-flex; align-items: center; justify-content: center; min-height: var(--nx-touch-min); width: fit-content; min-width: 0; padding: var(--space-xs) var(--space-md);
    overflow: hidden; border-radius: var(--radius-input); color: var(--color-on-brand); background: var(--nx-teal); font-weight: 700; text-overflow: ellipsis; white-space: nowrap;
  }
  [data-nx-component="nx-button"]::before { content: attr(data-label); }
  [data-nx-component="nx-row"]:has(> [data-nx-component="nx-column"] > [data-nx-component="nx-button"]) {
    grid-template-columns: repeat(auto-fit, minmax(132px, 1fr));
  }
  [data-nx-component="nx-row"]:has(> [data-nx-component="nx-column"] > [data-nx-component="nx-button"])
    > [data-nx-component="nx-column"] { grid-column: auto; min-width: 0; }
  [data-nx-component="nx-row"]:has(> [data-nx-component="nx-column"] > [data-nx-component="nx-button"])
    > [data-nx-component="nx-column"] > [data-nx-component="nx-button"] { width: 100%; }
  [data-nx-component="nx-row"]:has(> [data-nx-component="nx-button"]) {
    grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--space-xs);
  }
  [data-nx-component="nx-row"]:has(> [data-nx-component="nx-button"]) > [data-nx-component="nx-button"] {
    grid-column: auto; width: 100%;
  }
  [data-nx-component="nx-row"]:has(> [data-nx-component="nx-button"])
    > [data-nx-component="nx-button"]:last-child:nth-child(odd) { grid-column: 1 / -1; }
  [data-nx-component="nx-text"] { min-height: var(--nx-touch-min); padding: var(--space-xs) var(--space-3xs); }
  [data-nx-component="nx-text"]::before { content: attr(data-text); }
  [data-nx-component="nx-kpi"]::before { content: "KPI  ·  " attr(data-label); font-weight: 700; }
  [data-nx-component="nx-kpi"]::after { content: "-- " attr(data-unit); display: block; margin-top: var(--space-sm); color: var(--nx-teal-text); font-size: 24px; font-weight: 800; }
  [data-nx-component="nx-badge-widget"] { min-height: auto; padding: var(--space-xs) var(--space-sm); }
  [data-nx-component="nx-badge-widget"]::before { content: attr(data-label) "  상태"; color: var(--nx-teal-text); font-weight: 750; }
  [data-nx-component="nx-trend-chart"] { min-height: 170px; }
  [data-nx-component="nx-trend-chart"]::before { content: "트렌드 차트  ·  " attr(data-label); font-weight: 700; }
  [data-nx-component="nx-trend-chart"]::after {
    content: ""; position: absolute; inset: calc(var(--space-3xl) - var(--space-xs)) var(--space-md) var(--space-lg); border-left: 1px solid var(--nx-border); border-bottom: 1px solid var(--nx-border);
    background: linear-gradient(155deg, transparent 45%, var(--nx-teal) 46% 48%, transparent 49%);
  }
  @media (max-width: 720px) {
    body { padding: var(--space-sm); }
    [data-nx-component="nx-section"] { min-height: calc(100vh - 24px); padding: var(--space-sm); }
    [data-nx-component="nx-column"] { grid-column: 1 / -1 !important; }
    [data-nx-component="nx-row"]:has(> [data-nx-component="nx-button"]) { grid-template-columns: 1fr; }
    [data-nx-component="nx-row"]:has(> [data-nx-component="nx-button"]) > [data-nx-component="nx-button"] { grid-column: auto; }
  }
  @media (prefers-reduced-motion: reduce) { [data-nx-component] { transition: none; } }
`

export function buildEditorConfig(container: HTMLElement, blocksEl?: HTMLElement, traitsEl?: HTMLElement): EditorConfig {
  return {
    container,
    height: '100%',
    fromElement: false,
    storageManager: false,
    noticeOnUnload: false,
    showOffsetsSelected: true,
    canvas: { frameStyle: DESIGNER_CANVAS_STYLE },
    blockManager: { blocks: [], ...(blocksEl ? { appendTo: blocksEl } : {}) },
    traitManager: { ...(traitsEl ? { appendTo: traitsEl } : {}) },
    styleManager: { sectors: [] },
    panels: { defaults: [] },
  }
}

export interface BlockDef { id: string; label: string; description: string; category: string; content: GrapesNode }
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
  { type: 'nx-section', name: 'Section', allowedChildren: ['nx-row'], allowedParents: ['nx-column'] },
  { type: 'nx-row', name: 'Row', allowedChildren: ['nx-column', 'nx-button'], allowedParents: ['nx-section'] },
  { type: 'nx-column', name: 'Column', allowedChildren: ['nx-section', 'nx-grid', 'nx-form', 'nx-collection', 'nx-button', 'nx-text', 'nx-kpi', 'nx-badge-widget', 'nx-trend-chart'], allowedParents: ['nx-row'] },
  { type: 'nx-grid', name: 'DataGrid', allowedChildren: [], allowedParents: ['nx-column'] },
  { type: 'nx-form', name: 'Form', allowedChildren: ['nx-field'], allowedParents: ['nx-column'] },
  { type: 'nx-collection', name: 'Collection', allowedChildren: ['nx-field'], allowedParents: ['nx-column'] },
  { type: 'nx-field', name: 'Field', allowedChildren: [], allowedParents: ['nx-form', 'nx-collection'] },
  { type: 'nx-button', name: 'CommandButton', allowedChildren: [], allowedParents: ['nx-column', 'nx-row'] },
  { type: 'nx-text', name: 'Text', allowedChildren: [], allowedParents: ['nx-column'] },
  { type: 'nx-kpi', name: 'KPI', allowedChildren: [], allowedParents: ['nx-column'] },
  { type: 'nx-badge-widget', name: 'StatusBadge', allowedChildren: [], allowedParents: ['nx-column'] },
  { type: 'nx-trend-chart', name: 'TrendChart', allowedChildren: [], allowedParents: ['nx-column'] },
]

export const BLOCK_DEFS: BlockDef[] = [
  { id: 'nx-section', label: '섹션', description: '페이지 최상위 영역', category: '1. 레이아웃', content: { type: 'nx-section', attributes: {}, components: [] } },
  { id: 'nx-row', label: '행', description: '열을 담는 가로 영역', category: '1. 레이아웃', content: { type: 'nx-row', attributes: {}, components: [] } },
  { id: 'nx-column', label: '열', description: '위젯을 배치하는 12칸 열', category: '1. 레이아웃', content: { type: 'nx-column', attributes: { 'data-span': 6 }, components: [] } },
  { id: 'nx-form', label: '폼', description: '입력 필드와 저장 연결', category: '2. 입력·실행', content: { type: 'nx-form', attributes: {}, components: [] } },
  { id: 'nx-collection', label: '반복 항목', description: '여러 건을 추가·삭제하는 입력', category: '2. 입력·실행', content: { type: 'nx-collection', attributes: { 'data-collection-key': 'items', 'data-label': '항목 목록', 'data-item-label': '항목', 'data-min-items': 0 }, components: [] } },
  { id: 'nx-field', label: '필드', description: '텍스트·숫자·선택 입력', category: '2. 입력·실행', content: { type: 'nx-field', attributes: { 'data-field-key': '' } } },
  { id: 'nx-button', label: '명령 버튼', description: '저장·실행 명령 호출', category: '2. 입력·실행', content: { type: 'nx-button', attributes: { 'data-label': '버튼' } } },
  { id: 'nx-grid', label: '데이터 그리드', description: '조회 결과를 표로 표시', category: '3. 데이터 표현', content: { type: 'nx-grid', attributes: {} } },
  { id: 'nx-text', label: '텍스트', description: '설명과 안내 문구', category: '3. 데이터 표현', content: { type: 'nx-text', attributes: { 'data-text': '텍스트' } } },
  { id: 'nx-kpi', label: 'KPI 카드', description: '핵심 수치 한눈에 보기', category: '3. 데이터 표현', content: { type: 'nx-kpi', attributes: { 'data-label': 'KPI' } } },
  { id: 'nx-badge-widget', label: '상태 뱃지', description: '상태와 심각도 표시', category: '3. 데이터 표현', content: { type: 'nx-badge-widget', attributes: { 'data-label': '상태' } } },
  { id: 'nx-trend-chart', label: '트렌드 차트', description: '시간 흐름의 수치 변화', category: '3. 데이터 표현', content: { type: 'nx-trend-chart', attributes: { 'data-label': '트렌드' } } },
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

// FieldType(5종) 셀렉트 옵션 — layout.ts FieldType과 정확히 일치해야 한다(C# FieldType 미러).
const FIELD_TYPE_OPTS: TraitOption[] = ['Text', 'Number', 'Boolean', 'Date', 'Select'].map(t => ({ id: t, name: t }))
const FIELD_VALUE_GENERATOR_OPTS: TraitOption[] = ['None', 'UuidV4'].map(t => ({ id: t, name: t }))

export function buildTraitDefs(queries: QueryCatalog): Record<string, TraitDef[]> {
  const readOpts = queries.reads.map(query => ({ id: query.id, name: query.id }))
  const writeOpts = queries.writes.map(w => ({ id: w.id, name: w.id }))
  return {
    'nx-section': [{ type: 'text', name: 'data-title', label: '제목' }],
    'nx-row': [],
    'nx-column': [{ type: 'number', name: 'data-span', label: '폭(1-12)' }],
    'nx-grid': [
      { type: 'select', name: 'data-query-id', label: '조회 쿼리', options: readOpts },
      { type: 'text', name: 'data-selection-scope', label: '선택 모델 스코프' },
      { type: 'checkbox', name: 'data-selection-disabled', label: '행 선택 비활성화' },
      // 컬럼 작성 트레이트. JSON 인코딩 — key/caption에 콤마·콜론이 있어도 무손실 라운드트립. width=px(선택).
      { type: 'text', name: 'data-columns', label: '컬럼(JSON: [{key,caption,visible,width}])' },
    ],
    'nx-form': [
      { type: 'select', name: 'data-save-query-id', label: '저장 쿼리', options: writeOpts },
      { type: 'text', name: 'data-binding-scope', label: '바인딩 모델 스코프' },
      // Phase-2 멀티폼 — 체크 시 폼 전용 모델 격리(폼별 저장/검증). 미체크=화면 공유 모델(하위호환).
      { type: 'checkbox', name: 'data-isolated', label: '모델 격리(멀티폼)' },
    ],
    'nx-collection': [
      { type: 'text', name: 'data-collection-key', label: '모델 컬렉션 키' },
      { type: 'text', name: 'data-binding-scope', label: '바인딩 모델 스코프' },
      { type: 'text', name: 'data-label', label: '목록 라벨' },
      { type: 'text', name: 'data-item-label', label: '항목 라벨' },
      { type: 'number', name: 'data-min-items', label: '최소 항목 수' },
      { type: 'number', name: 'data-max-items', label: '최대 항목 수(선택)' },
    ],
    // FieldDefinition 전체를 이산 트레이트로 노출(data-field JSON blob 폐기, 트레이트 패널이 단일 편집 출처).
    'nx-field': [
      { type: 'text', name: 'data-field-key', label: '필드 키' },
      { type: 'text', name: 'data-field-label', label: '라벨' },
      { type: 'select', name: 'data-field-type', label: '타입', options: FIELD_TYPE_OPTS },
      { type: 'checkbox', name: 'data-field-required', label: '필수' },
      { type: 'checkbox', name: 'data-field-readonly', label: '읽기전용' },
      { type: 'checkbox', name: 'data-field-hidden', label: '숨김(모델 전용)' },
      { type: 'select', name: 'data-field-value-generator', label: '자동 값 생성', options: FIELD_VALUE_GENERATOR_OPTS },
      { type: 'text', name: 'data-field-options', label: '옵션(JSON 배열, Select용)' },
      { type: 'text', name: 'data-field-options-query', label: '옵션 쿼리 ID(동적 Select)' },
    ],
    'nx-button': [
      { type: 'text', name: 'data-label', label: '라벨' },
      { type: 'select', name: 'data-command', label: '명령 쿼리', options: writeOpts },
      { type: 'text', name: 'data-confirm', label: '확인 문구(파괴적 명령용)' },
      { type: 'text', name: 'data-binding-scope', label: '명령 모델 스코프' },
    ],
    'nx-text': [{ type: 'text', name: 'data-text', label: '텍스트' }],
    'nx-kpi': [
      { type: 'text', name: 'data-label', label: '라벨' },
      { type: 'select', name: 'data-query-id', label: '조회 쿼리', options: readOpts },
      { type: 'text', name: 'data-value-column', label: '값 컬럼' },
      { type: 'text', name: 'data-unit', label: '단위(선택)' },
      { type: 'text', name: 'data-link-uiid', label: '드릴다운 화면 UI_ID(선택)' },
    ],
    // styles(LIST 서브필드)는 JSON 인코딩 — 심각도는 success|warning|danger|info|neutral(C# 화이트리스트 미러).
    'nx-badge-widget': [
      { type: 'text', name: 'data-label', label: '라벨(선택)' },
      { type: 'select', name: 'data-query-id', label: '조회 쿼리', options: readOpts },
      { type: 'text', name: 'data-value-column', label: '값 컬럼' },
      { type: 'text', name: 'data-styles', label: '규칙(JSON: [{value,severity,displayText}])' },
    ],
    'nx-trend-chart': [
      { type: 'text', name: 'data-label', label: '라벨' },
      { type: 'select', name: 'data-query-id', label: '조회 쿼리', options: readOpts },
      { type: 'text', name: 'data-value-column', label: '값 컬럼(수치, 단일)' },
      { type: 'text', name: 'data-value-columns', label: '값 컬럼(다중, 콤마 구분 — 범례)' },
      { type: 'number', name: 'data-max-points', label: '최대 포인트(기본 50)' },
      { type: 'text', name: 'data-time-column', label: '시간 컬럼(선택, 축 라벨)' },
    ],
  }
}

/** GrapesJS Component 중 권한 자동 동기화에 필요한 최소 표면. 순수 단위테스트에서 실제 editor 없이 사용한다. */
export interface PermissionSyncComponent extends TypeMatchable {
  getAttributes(): Record<string, unknown>
  addAttributes(attributes: Record<string, unknown>): unknown
  removeAttributes(attribute: string | string[]): unknown
}

interface PermissionBindingSpec {
  type: string
  attribute: string
  catalog: keyof QueryCatalog
}

const PERMISSION_BINDINGS: PermissionBindingSpec[] = [
  { type: 'nx-grid', attribute: 'data-query-id', catalog: 'reads' },
  { type: 'nx-kpi', attribute: 'data-query-id', catalog: 'reads' },
  { type: 'nx-badge-widget', attribute: 'data-query-id', catalog: 'reads' },
  { type: 'nx-trend-chart', attribute: 'data-query-id', catalog: 'reads' },
  { type: 'nx-field', attribute: 'data-field-options-query', catalog: 'reads' },
  { type: 'nx-form', attribute: 'data-save-query-id', catalog: 'writes' },
  { type: 'nx-button', attribute: 'data-command', catalog: 'writes' },
]

function descriptorFor(id: string, descriptors: QueryDescriptor[]): QueryDescriptor | undefined {
  const normalized = id.trim().toLowerCase()
  return descriptors.find(item => item.id.trim().toLowerCase() === normalized)
}

/**
 * 현재 component binding이 요구하는 권한을 반환한다.
 * undefined=권한 자동화 대상이 아닌 component, null=빈/미등록/public binding(기존 권한 제거), string=동기화 값.
 */
export function requiredPermissionForBinding(
  component: PermissionSyncComponent,
  queries: QueryCatalog,
): string | null | undefined {
  const spec = PERMISSION_BINDINGS.find(item => component.is(item.type))
  if (!spec) return undefined

  const raw = component.getAttributes()[spec.attribute]
  const id = typeof raw === 'string' ? raw.trim() : ''
  if (id.length === 0) return null

  const permission = descriptorFor(id, queries[spec.catalog])?.requiredPermission
  return typeof permission === 'string' && permission.trim().length > 0 ? permission.trim() : null
}

/**
 * 쿼리/명령 선택이 바뀔 때 data-required-permission을 카탈로그 값으로 맞춘다.
 * public·미등록·선택 해제는 과거 binding의 stale permission을 반드시 제거한다.
 */
export function syncRequiredPermission(
  component: PermissionSyncComponent,
  queries: QueryCatalog,
): boolean {
  const expected = requiredPermissionForBinding(component, queries)
  if (expected === undefined) return false

  const attributes = component.getAttributes()
  const current = typeof attributes['data-required-permission'] === 'string'
    ? attributes['data-required-permission'].trim()
    : null
  if (expected !== null) {
    if (current === expected) return false
    component.addAttributes({ 'data-required-permission': expected })
    return true
  }

  if (!Object.prototype.hasOwnProperty.call(attributes, 'data-required-permission')) return false
  component.removeAttributes('data-required-permission')
  return true
}
