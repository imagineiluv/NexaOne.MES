import type {
  LayoutNode, GrapesNode, FieldDefinition, FieldType, GridColumnDefinition,
  FieldWidget, ScreenDefinitionDto,
} from './layout'

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
      // columns → 트레이트 편집 가능한 이산 spec 문자열(data-columns JSON blob 폐기, 단일 출처=트레이트).
      if (node.columns != null) attributes['data-columns-spec'] = columnsToSpec(node.columns)
      break
    case 'form':
      if (node.saveQueryId != null) attributes['data-save-query-id'] = node.saveQueryId
      comp.components = (node.fields ?? []).map(layoutToComponent)
      break
    case 'field': {
      // FieldDefinition 전체를 이산 data-field-* 속성으로 분해(data-field JSON blob 폐기, 단일 출처=트레이트).
      // 의미 있는 값만 기록(부재 옵셔널은 빈 문자열 미기록 — 기존 조건부 속성 스타일 유지).
      const f = node.field
      const key = f?.key ?? node.fieldKey ?? undefined
      if (key != null) attributes['data-field-key'] = key
      if (f != null) {
        if (f.label != null) attributes['data-field-label'] = f.label
        if (f.type != null) attributes['data-field-type'] = f.type
        if (f.required) attributes['data-field-required'] = true
        if (f.readOnly) attributes['data-field-readonly'] = true
        if (f.options != null && f.options.length > 0) attributes['data-field-options'] = f.options.join(',')
      }
      break
    }
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
function bool(a: Record<string, unknown> | undefined, k: string): boolean {
  const v = a?.[k]
  if (typeof v === 'boolean') return v
  if (typeof v === 'string') return v === 'true' || v === '1'
  return false
}

const FIELD_TYPES: readonly FieldType[] = ['Text', 'Number', 'Boolean', 'Date', 'Select']
function asFieldType(v: string | undefined): FieldType {
  return v != null && (FIELD_TYPES as readonly string[]).includes(v) ? (v as FieldType) : 'Text'
}

// columns ↔ data-columns-spec 직렬화. 형식: 콤마 구분 `key:caption` 쌍, visible=false면 `:hidden` 부가.
// 예) `code:코드, name:이름, secret:비밀:hidden`. 한계(Phase 2 인정): 콤마(쌍 구분자)·콜론(필드 구분자)을
// 포함한 key/caption은 표현 불가 — 그런 경우 JSON 평면 임포트(flatToLayout) 경로를 쓴다.
function columnsToSpec(cols: GridColumnDefinition[]): string {
  return cols.map(c => {
    const base = `${c.key}:${c.caption}`
    return c.visible === false ? `${base}:hidden` : base
  }).join(', ')
}
function specToColumns(spec: string | undefined): GridColumnDefinition[] {
  if (spec == null) return []
  return spec.split(',').map(s => s.trim()).filter(s => s.length > 0).map(s => {
    const parts = s.split(':').map(p => p.trim())
    const [key, caption, flag] = parts
    return { key, caption: caption ?? key, visible: flag !== 'hidden' }
  })
}

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
      const spec = str(a, 'data-columns-spec')
      const columns = spec != null ? specToColumns(spec) : undefined
      return { kind, ...base, ...(queryId != null ? { queryId } : {}), ...(columns != null ? { columns } : {}) }
    }
    case 'form': {
      const saveQueryId = str(a, 'data-save-query-id')
      const fields = childNodes.filter((n): n is FieldWidget => n.kind === 'field')
      return { kind, ...base, ...(saveQueryId != null ? { saveQueryId } : {}), fields }
    }
    case 'field': {
      const fieldKey = str(a, 'data-field-key')
      // 이산 data-field-* 속성에서 FieldDefinition 조립. label/type/options 등 키 외 속성이 하나라도 있으면
      // 완전한 field를 만든다(key→fieldKey 폴백, label→key 폴백, type 기본 Text, required/readOnly 기본 false,
      // options는 콤마 구분·빈 값이면 null). 키만 있는 베어 필드는 field 없이 fieldKey만 유지(하위호환).
      const label = str(a, 'data-field-label')
      const type = str(a, 'data-field-type')
      const optsRaw = str(a, 'data-field-options')
      const required = bool(a, 'data-field-required')
      const readOnly = bool(a, 'data-field-readonly')
      const hasFieldAttr = label != null || type != null || optsRaw != null || required || readOnly
      let field: FieldDefinition | undefined
      if (hasFieldAttr) {
        const key = fieldKey ?? ''
        const options = optsRaw != null && optsRaw.trim().length > 0
          ? optsRaw.split(',').map(o => o.trim()).filter(o => o.length > 0)
          : null
        field = {
          key, label: label ?? key, type: asFieldType(type), required, readOnly, options,
        }
      }
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

export function buildDefinitionJson(uiId: string, title: string, layout: LayoutNode | null): string {
  const dto: ScreenDefinitionDto = {
    uiId, title, fields: [], columns: null, queryId: null, saveQueryId: null,
    layout: layout ?? null,
  }
  return JSON.stringify(dto)
}

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
