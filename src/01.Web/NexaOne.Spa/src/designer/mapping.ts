import type {
  LayoutNode, GrapesNode, FieldDefinition, GridColumnDefinition,
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

export function buildDefinitionJson(uiId: string, title: string, layout: LayoutNode | null): string {
  const dto: ScreenDefinitionDto = {
    uiId, title, fields: [], columns: null, queryId: null, saveQueryId: null,
    layout: layout ?? null,
  }
  return JSON.stringify(dto)
}

export function parseDefinition(json: string): { title: string; layout: LayoutNode | null } {
  try {
    const dto = JSON.parse(json) as Partial<ScreenDefinitionDto>
    return { title: dto.title ?? '', layout: (dto.layout as LayoutNode | undefined) ?? null }
  } catch {
    return { title: '', layout: null }
  }
}
