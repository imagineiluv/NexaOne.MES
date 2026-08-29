import type {
  LayoutNode, GrapesNode, FieldDefinition, FieldType, GridColumnDefinition,
  FieldWidget, QueryCatalog, QueryDescriptor, ScreenDefinitionDto, BadgeStyleRule, ScreenPurpose,
  FieldValueGenerator,
} from './layout'
import { FIELD_VALUE_GENERATOR_VALUES } from './layout'

const KIND_TO_TYPE: Record<LayoutNode['kind'], string> = {
  section: 'nx-section', row: 'nx-row', column: 'nx-column', grid: 'nx-grid',
  form: 'nx-form', field: 'nx-field', collection: 'nx-collection', commandButton: 'nx-button', text: 'nx-text', kpi: 'nx-kpi',
  statusBadge: 'nx-badge-widget', trendChart: 'nx-trend-chart',
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
  // GrapesJS iframe의 편집 전용 프리뷰 스타일 표식. 저장 스키마로 환원할 때는 무시되므로
  // 런타임 정의를 오염시키지 않으면서 빈 위젯도 종류와 드롭 영역을 시각화할 수 있다.
  attributes['data-nx-component'] = KIND_TO_TYPE[node.kind]
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
      if (node.selectionScope != null && node.selectionScope !== '') attributes['data-selection-scope'] = node.selectionScope
      if (node.selectionDisabled) attributes['data-selection-disabled'] = true
      // columns(LIST 서브필드)는 JSON으로 인코딩 — key/caption에 콤마·콜론이 있어도 손실 없이 라운드트립한다.
      // (이산 spec 문자열 인코딩은 구분자 포함 값에서 무손실 보장 불가 → JSON으로 환원.)
      if (node.columns != null && node.columns.length > 0) attributes['data-columns'] = JSON.stringify(node.columns)
      break
    case 'form':
      if (node.saveQueryId != null) attributes['data-save-query-id'] = node.saveQueryId
      if (node.isolated) attributes['data-isolated'] = true
      if (node.bindingScope != null && node.bindingScope !== '') attributes['data-binding-scope'] = node.bindingScope
      comp.components = (node.fields ?? []).map(layoutToComponent)
      break
    case 'collection':
      attributes['data-collection-key'] = node.collectionKey
      attributes['data-label'] = node.label
      attributes['data-item-label'] = node.itemLabel
      if (node.bindingScope != null && node.bindingScope !== '') attributes['data-binding-scope'] = node.bindingScope
      if (node.minItems != null) attributes['data-min-items'] = node.minItems
      if (node.maxItems != null) attributes['data-max-items'] = node.maxItems
      comp.components = (node.fields ?? []).map(layoutToComponent)
      break
    case 'field': {
      // FieldDefinition을 속성으로 분해(data-field JSON blob 폐기, 단일 출처=트레이트). SCALAR 서브필드는
      // 이산 data-field-* 속성(라운드트립 안전), LIST 서브필드(options)만 JSON 인코딩(구분자 무손실).
      // 의미 있는 값만 기록(부재 옵셔널은 빈 문자열 미기록 — 기존 조건부 속성 스타일 유지).
      const f = node.field
      const key = f?.key ?? node.fieldKey ?? undefined
      if (key != null) attributes['data-field-key'] = key
      if (f != null) {
        if (f.label != null) attributes['data-field-label'] = f.label
        if (f.type != null) attributes['data-field-type'] = f.type
        if (f.required) attributes['data-field-required'] = true
        if (f.readOnly) attributes['data-field-readonly'] = true
        if (f.hidden != null) attributes['data-field-hidden'] = f.hidden
        if (f.valueGenerator != null) attributes['data-field-value-generator'] = f.valueGenerator
        // options(LIST 서브필드)는 JSON으로 인코딩 — 값에 콤마가 있어도 손실 없이 라운드트립한다(콤마-조인 폐기).
        if (f.options != null && f.options.length > 0) attributes['data-field-options'] = JSON.stringify(f.options)
        if (f.optionsQueryId != null && f.optionsQueryId !== '') attributes['data-field-options-query'] = f.optionsQueryId
      }
      break
    }
    case 'commandButton':
      attributes['data-label'] = node.label
      if (node.command != null) attributes['data-command'] = node.command
      if (node.confirmMessage != null && node.confirmMessage !== '') attributes['data-confirm'] = node.confirmMessage
      if (node.bindingScope != null && node.bindingScope !== '') attributes['data-binding-scope'] = node.bindingScope
      break
    case 'text':
      attributes['data-text'] = node.text
      if (node.isLabel) attributes['data-is-label'] = true
      break
    case 'kpi':
      attributes['data-label'] = node.label
      if (node.queryId != null) attributes['data-query-id'] = node.queryId
      if (node.valueColumn != null) attributes['data-value-column'] = node.valueColumn
      if (node.unit != null) attributes['data-unit'] = node.unit
      if (node.linkUiId != null && node.linkUiId !== '') attributes['data-link-uiid'] = node.linkUiId
      break
    case 'statusBadge':
      if (node.label != null) attributes['data-label'] = node.label
      if (node.queryId != null) attributes['data-query-id'] = node.queryId
      if (node.valueColumn != null) attributes['data-value-column'] = node.valueColumn
      // styles(LIST 서브필드)는 JSON 인코딩 — value/displayText에 구분자가 있어도 무손실(columns/options 선례).
      if (node.styles != null && node.styles.length > 0) attributes['data-styles'] = JSON.stringify(node.styles)
      break
    case 'trendChart':
      attributes['data-label'] = node.label
      if (node.queryId != null) attributes['data-query-id'] = node.queryId
      if (node.valueColumn != null) attributes['data-value-column'] = node.valueColumn
      if (node.valueColumns != null && node.valueColumns.length > 0) attributes['data-value-columns'] = node.valueColumns.join(',')
      if (node.maxPoints != null) attributes['data-max-points'] = node.maxPoints
      if (node.timeColumn != null && node.timeColumn !== '') attributes['data-time-column'] = node.timeColumn
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
// 문자열 속성을 관용적으로 JSON 파싱(비문자열·파싱실패→undefined). LIST 서브필드(options/columns)의
// 무손실 인코딩 복원에 사용 — 구분자 포함 값도 정확히 환원한다.
function jsonAttr<T>(a: Record<string, unknown> | undefined, k: string): T | undefined {
  const v = a?.[k]
  if (typeof v !== 'string') return undefined
  try { return JSON.parse(v) as T } catch { return undefined }
}
// 비어있지 않은 문자열 배열만 반환(그 외엔 null) — Select 옵션 복원용 관용 파싱.
function strArrayAttr(a: Record<string, unknown> | undefined, k: string): string[] | null {
  const v = jsonAttr<unknown>(a, k)
  if (Array.isArray(v) && v.length > 0 && v.every(e => typeof e === 'string')) return v as string[]
  return null
}

const FIELD_TYPES: readonly FieldType[] = ['Text', 'Number', 'Boolean', 'Date', 'Select']
function asFieldType(v: string | undefined): FieldType {
  return v != null && (FIELD_TYPES as readonly string[]).includes(v) ? (v as FieldType) : 'Text'
}

function asFieldValueGenerator(v: string | undefined): FieldValueGenerator {
  return v != null && (FIELD_VALUE_GENERATOR_VALUES as readonly string[]).includes(v)
    ? (v as FieldValueGenerator)
    : 'None'
}

function finiteNumber(a: Record<string, unknown> | undefined, key: string): number | undefined {
  const raw = a?.[key]
  if (raw === '' || raw == null) return undefined
  const value = typeof raw === 'number' ? raw : Number(raw)
  return Number.isFinite(value) ? value : undefined
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
      const selectionScope = str(a, 'data-selection-scope')
      // columns는 data-columns(JSON) 관용 파싱 → {key,caption,visible} 배열. 비배열·파싱실패면 undefined(복원 안 함).
      const columns = jsonAttr<GridColumnDefinition[]>(a, 'data-columns')
      return {
        kind, ...base,
        ...(queryId != null ? { queryId } : {}),
        ...(Array.isArray(columns) ? { columns } : {}),
        ...(selectionScope != null ? { selectionScope } : {}),
        ...(bool(a, 'data-selection-disabled') ? { selectionDisabled: true } : {}),
      }
    }
    case 'form': {
      const saveQueryId = str(a, 'data-save-query-id')
      const bindingScope = str(a, 'data-binding-scope')
      const fields = childNodes.filter((n): n is FieldWidget => n.kind === 'field')
      return {
        kind, ...base, ...(saveQueryId != null ? { saveQueryId } : {}), fields,
        ...(bool(a, 'data-isolated') ? { isolated: true } : {}),
        ...(bindingScope != null ? { bindingScope } : {}),
      }
    }
    case 'collection': {
      const fields = childNodes.filter((node): node is FieldWidget => node.kind === 'field')
      const collectionKey = str(a, 'data-collection-key') ?? ''
      const label = str(a, 'data-label') ?? '항목 목록'
      const itemLabel = str(a, 'data-item-label') ?? '항목'
      const minItems = finiteNumber(a, 'data-min-items')
      const maxItems = finiteNumber(a, 'data-max-items')
      const bindingScope = str(a, 'data-binding-scope')
      return {
        kind, ...base, collectionKey, label, itemLabel, fields,
        ...(bindingScope != null ? { bindingScope } : {}),
        ...(minItems != null ? { minItems } : {}),
        ...(maxItems != null ? { maxItems } : {}),
      }
    }
    case 'field': {
      const fieldKey = str(a, 'data-field-key')
      // 이산 data-field-* 속성에서 FieldDefinition 조립. label/type/options 등 키 외 속성이 하나라도 있으면
      // 완전한 field를 만든다(key→fieldKey 폴백, label→key 폴백, type 기본 Text, required/readOnly 기본 false,
      // options는 JSON 배열 파싱·빈/실패면 null). 키만 있는 베어 필드는 field 없이 fieldKey만 유지(하위호환).
      const label = str(a, 'data-field-label')
      const type = str(a, 'data-field-type')
      const optsRaw = str(a, 'data-field-options')
      const optionsQueryId = str(a, 'data-field-options-query')
      const required = bool(a, 'data-field-required')
      const readOnly = bool(a, 'data-field-readonly')
      const hiddenSpecified = Object.prototype.hasOwnProperty.call(a ?? {}, 'data-field-hidden')
      const hidden = bool(a, 'data-field-hidden')
      const generatorRaw = str(a, 'data-field-value-generator')
      const hasFieldAttr = label != null || type != null || optsRaw != null || optionsQueryId != null
        || required || readOnly || hiddenSpecified || generatorRaw != null
      let field: FieldDefinition | undefined
      if (hasFieldAttr) {
        const key = fieldKey ?? ''
        // options는 data-field-options(JSON 배열) 관용 파싱 → 비어있지 않은 문자열 배열이면 사용, 아니면 null.
        const options = strArrayAttr(a, 'data-field-options')
        field = {
          key, label: label ?? key, type: asFieldType(type), required, readOnly, options,
          ...(hiddenSpecified ? { hidden } : {}),
          ...(generatorRaw != null ? { valueGenerator: asFieldValueGenerator(generatorRaw) } : {}),
          // 부재 시 속성 자체를 만들지 않는다(기존 라운드트립 픽스처 불변) — 존재할 때만 미러.
          ...(optionsQueryId != null ? { optionsQueryId } : {}),
        }
      }
      return { kind, ...base, ...(fieldKey != null ? { fieldKey } : {}), ...(field != null ? { field } : {}) }
    }
    case 'commandButton': {
      const command = str(a, 'data-command')
      const confirmMessage = str(a, 'data-confirm')
      const bindingScope = str(a, 'data-binding-scope')
      return {
        kind, ...base, label: str(a, 'data-label') ?? '',
        ...(command != null ? { command } : {}),
        ...(confirmMessage != null ? { confirmMessage } : {}),
        ...(bindingScope != null ? { bindingScope } : {}),
      }
    }
    case 'text':
      return { kind, ...base, text: str(a, 'data-text') ?? '', ...(a?.['data-is-label'] ? { isLabel: true } : {}) }
    case 'kpi': {
      const queryId = str(a, 'data-query-id')
      const valueColumn = str(a, 'data-value-column')
      const unit = str(a, 'data-unit')
      const linkUiId = str(a, 'data-link-uiid')
      return {
        kind, ...base, label: str(a, 'data-label') ?? '',
        ...(queryId != null ? { queryId } : {}),
        ...(valueColumn != null ? { valueColumn } : {}),
        ...(unit != null ? { unit } : {}),
        ...(linkUiId != null ? { linkUiId } : {}),
      }
    }
    case 'statusBadge': {
      const label = str(a, 'data-label')
      const queryId = str(a, 'data-query-id')
      const valueColumn = str(a, 'data-value-column')
      const styles = jsonAttr<BadgeStyleRule[]>(a, 'data-styles')
      return {
        kind, ...base,
        ...(label != null ? { label } : {}),
        ...(queryId != null ? { queryId } : {}),
        ...(valueColumn != null ? { valueColumn } : {}),
        ...(Array.isArray(styles) ? { styles } : {}),
      }
    }
    case 'trendChart': {
      const queryId = str(a, 'data-query-id')
      const valueColumn = str(a, 'data-value-column')
      const timeColumn = str(a, 'data-time-column')
      const rawCols = str(a, 'data-value-columns')
      const valueColumns = rawCols != null ? rawCols.split(',').map(s => s.trim()).filter(s => s.length > 0) : undefined
      const rawMax = a?.['data-max-points']
      const maxPoints = typeof rawMax === 'number' ? rawMax : (typeof rawMax === 'string' && rawMax !== '' ? Number(rawMax) : undefined)
      return {
        kind, ...base, label: str(a, 'data-label') ?? '',
        ...(queryId != null ? { queryId } : {}),
        ...(valueColumn != null ? { valueColumn } : {}),
        ...(valueColumns != null && valueColumns.length > 0 ? { valueColumns } : {}),
        ...(maxPoints != null && !Number.isNaN(maxPoints) ? { maxPoints } : {}),
        ...(timeColumn != null ? { timeColumn } : {}),
      }
    }
  }
}

export function buildDefinitionJson(
  uiId: string, title: string, layout: LayoutNode | null,
  refreshIntervalSeconds?: number | null,
  searchFields?: FieldDefinition[] | null,
  countQueryId?: string | null,
  deleteQueryId?: string | null,
  bulkCommands?: import('./layout').BulkCommandDefinition[] | null,
  purpose?: ScreenPurpose | null,
  readRequiredPermission?: string | null,
  saveRequiredPermission?: string | null,
  deleteRequiredPermission?: string | null,
): string {
  const layoutErrors = validateLayoutStructure(layout)
  if (layoutErrors.length > 0) {
    throw new Error(`화면 레이아웃 구조가 올바르지 않습니다: ${layoutErrors.join(' / ')}`)
  }
  const dto: ScreenDefinitionDto = {
    uiId, title, purpose: purpose ?? 'Auto', fields: [], columns: null, queryId: null, saveQueryId: null,
    layout: layout ?? null,
    // 화면 수준 설정 보존 — 디자이너 재저장이 자동 새로고침/검색 조건/서버 페이징 설정을 드랍하지 않게 한다(손실 방지).
    ...(refreshIntervalSeconds != null ? { refreshIntervalSeconds } : {}),
    ...(searchFields != null && searchFields.length > 0 ? { searchFields } : {}),
    ...(countQueryId != null && countQueryId.length > 0 ? { countQueryId } : {}),
    ...(deleteQueryId != null && deleteQueryId.length > 0 ? { deleteQueryId } : {}),
    ...(bulkCommands != null && bulkCommands.length > 0 ? { bulkCommands } : {}),
    ...(readRequiredPermission != null && readRequiredPermission.length > 0 ? { readRequiredPermission } : {}),
    ...(saveRequiredPermission != null && saveRequiredPermission.length > 0 ? { saveRequiredPermission } : {}),
    ...(deleteRequiredPermission != null && deleteRequiredPermission.length > 0 ? { deleteRequiredPermission } : {}),
  }
  return JSON.stringify(dto)
}

/** 서버 저장 검증과 같은 collection 구조 불변식을 Designer 저장 직전에 빠르게 안내한다. */
export function validateLayoutStructure(layout: LayoutNode | null): string[] {
  if (layout == null) return []
  const errors: string[] = []

  const visit = (node: LayoutNode, path: string) => {
    if (node.kind === 'collection') {
      const minimum = node.minItems ?? 0
      if (typeof node.collectionKey !== 'string' || node.collectionKey.trim().length === 0) {
        errors.push(`${path}.collectionKey가 비어 있습니다.`)
      }
      if (!Number.isInteger(minimum) || minimum < 0) errors.push(`${path}.minItems는 0 이상의 정수여야 합니다.`)
      if (node.maxItems != null
          && (!Number.isInteger(node.maxItems) || node.maxItems < 0 || node.maxItems < minimum)) {
        errors.push(`${path}.maxItems는 minItems 이상의 정수여야 합니다.`)
      }
      return
    }

    const children = node.kind === 'section' || node.kind === 'row' || node.kind === 'column'
      ? (node.children ?? [])
      : []
    children.forEach((child, index) => visit(child, `${path}.children[${index}]`))
  }

  visit(layout, 'layout')
  return errors
}

export function parseDefinition(json: string): { title: string; layout: LayoutNode | null; flat: ScreenDefinitionDto | null } {
  try {
    const dto = JSON.parse(json) as Partial<ScreenDefinitionDto>
    const flat: ScreenDefinitionDto = {
      uiId: dto.uiId ?? '', title: dto.title ?? '', purpose: dto.purpose ?? 'Auto',
      fields: Array.isArray(dto.fields) ? dto.fields : [],
      columns: dto.columns ?? null, queryId: dto.queryId ?? null, saveQueryId: dto.saveQueryId ?? null,
      layout: (dto.layout as LayoutNode | undefined) ?? null,
      refreshIntervalSeconds: dto.refreshIntervalSeconds ?? null,
      searchFields: Array.isArray(dto.searchFields) && dto.searchFields.length > 0 ? dto.searchFields : null,
      countQueryId: dto.countQueryId ?? null,
      deleteQueryId: dto.deleteQueryId ?? null,
      bulkCommands: Array.isArray(dto.bulkCommands) && dto.bulkCommands.length > 0 ? dto.bulkCommands : null,
      readRequiredPermission: dto.readRequiredPermission ?? null,
      saveRequiredPermission: dto.saveRequiredPermission ?? null,
      deleteRequiredPermission: dto.deleteRequiredPermission ?? null,
    }
    return { title: flat.title, layout: flat.layout ?? null, flat }
  } catch {
    return { title: '', layout: null, flat: null }
  }
}

// 레거시 평면 정의(layout 없음)를 디자이너 편집용 기본 LayoutNode 트리로 합성(단방향, Phase 2).
// columns(1개↑)→그리드; fields(1개↑)→폼(+saveQueryId면 저장버튼); 둘 다면 2열(7/5), 하나면 12, 둘 다 없으면 빈 섹션.
function bindingPermission(
  declared: string | null | undefined,
  bindingId: string | null | undefined,
  descriptors: QueryDescriptor[] | undefined,
): string | undefined {
  const explicit = typeof declared === 'string' ? declared.trim() : ''
  if (explicit.length > 0) return explicit
  const id = typeof bindingId === 'string' ? bindingId.trim().toLowerCase() : ''
  if (id.length === 0) return undefined
  const catalog = descriptors?.find(item => item.id.trim().toLowerCase() === id)?.requiredPermission
  return typeof catalog === 'string' && catalog.trim().length > 0 ? catalog.trim() : undefined
}

export function flatToLayout(dto: ScreenDefinitionDto, queries?: QueryCatalog): LayoutNode {
  const uid = dto.uiId && dto.uiId.length > 0 ? dto.uiId : 'gen'
  const hasGrid = Array.isArray(dto.columns) && dto.columns.length > 0
  const hasForm = Array.isArray(dto.fields) && dto.fields.length > 0
  if (!hasGrid && !hasForm)
    return { kind: 'section', id: `sec-${uid}`, ...(dto.title ? { title: dto.title } : {}), children: [] }

  const cols: LayoutNode[] = []
  if (hasGrid) {
    const requiredPermission = bindingPermission(dto.readRequiredPermission, dto.queryId, queries?.reads)
    const grid: LayoutNode = {
      kind: 'grid', id: `grid-${uid}`, queryId: dto.queryId ?? null,
      columns: dto.columns as GridColumnDefinition[],
      ...(requiredPermission ? { requiredPermission } : {}),
    }
    cols.push({ kind: 'column', id: `col-grid-${uid}`, span: hasForm ? 7 : 12, children: [grid] })
  }
  if (hasForm) {
    const fields: FieldWidget[] = (dto.fields).map((f: FieldDefinition) => {
      const requiredPermission = bindingPermission(null, f.optionsQueryId, queries?.reads)
      return {
        kind: 'field', id: `fld-${f.key}`, fieldKey: f.key, field: f,
        ...(requiredPermission ? { requiredPermission } : {}),
      }
    })
    const requiredPermission = bindingPermission(dto.saveRequiredPermission, dto.saveQueryId, queries?.writes)
    const formChildren: LayoutNode[] = [{
      kind: 'form', id: `form-${uid}`, saveQueryId: dto.saveQueryId ?? null, fields,
      ...(requiredPermission ? { requiredPermission } : {}),
    }]
    if (dto.saveQueryId) formChildren.push({
      kind: 'commandButton', id: `btn-save-${uid}`, label: '저장', command: dto.saveQueryId,
      ...(requiredPermission ? { requiredPermission } : {}),
    })
    cols.push({ kind: 'column', id: `col-form-${uid}`, span: hasGrid ? 5 : 12, children: formChildren })
  }
  return { kind: 'section', id: `sec-${uid}`, ...(dto.title ? { title: dto.title } : {}), children: [{ kind: 'row', id: `row-${uid}`, children: cols }] }
}
