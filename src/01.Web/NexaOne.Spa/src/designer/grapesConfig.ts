// GrapesJS 디자이너의 순수 설정/데이터(블록·컴포넌트 type·트레이트·init 옵션). 실제 인스턴스 생성은
// ScreenEditor.tsx 글루에서만 일어난다. 잠금 의도: 기본 블록/RTE/스타일 비노출, 8개 type만 등록, 중첩 규칙.
import type { GrapesNode } from './layout'

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

export function buildEditorConfig(container: HTMLElement): EditorConfig {
  return {
    container,
    height: '100%',
    fromElement: false,
    storageManager: false,
    rte: false,
    blockManager: { blocks: [] },
    styleManager: { sectors: [] },
    panels: { defaults: [] },
  }
}

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
