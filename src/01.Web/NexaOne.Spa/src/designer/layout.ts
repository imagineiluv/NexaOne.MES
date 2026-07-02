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

export type LayoutNode =
  | SectionNode | RowNode | ColumnNode
  | GridWidget | FormWidget | FieldWidget | ButtonWidget | TextWidget | KpiWidget

interface NodeBase {
  id?: string
  requiredPermission?: string | null
}
export interface SectionNode extends NodeBase { kind: 'section'; title?: string; children?: LayoutNode[] }
export interface RowNode     extends NodeBase { kind: 'row'; children?: LayoutNode[] }
export interface ColumnNode  extends NodeBase { kind: 'column'; span: number; children?: LayoutNode[] }
export interface GridWidget  extends NodeBase { kind: 'grid'; queryId?: string | null; columns?: GridColumnDefinition[] }
export interface FormWidget  extends NodeBase { kind: 'form'; saveQueryId?: string | null; fields?: FieldWidget[] }
export interface FieldWidget extends NodeBase { kind: 'field'; fieldKey?: string | null; field?: FieldDefinition | null }
export interface ButtonWidget extends NodeBase { kind: 'commandButton'; label: string; command?: string | null }
export interface TextWidget  extends NodeBase { kind: 'text'; text: string; isLabel?: boolean }
// KPI 카드(Phase-2) — 바인딩 쿼리 첫 행의 valueColumn 값을 큰 숫자로 표시(C# KpiWidget 미러).
export interface KpiWidget   extends NodeBase { kind: 'kpi'; label: string; queryId?: string | null; valueColumn?: string | null; unit?: string | null }

export interface ScreenDefinitionDto {
  uiId: string
  title: string
  fields: FieldDefinition[]
  columns?: GridColumnDefinition[] | null
  queryId?: string | null
  saveQueryId?: string | null
  layout?: LayoutNode | null
}

export interface GrapesNode {
  type?: string
  attributes?: Record<string, unknown>
  components?: GrapesNode[]
}
