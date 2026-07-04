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
  /** Select 옵션의 동적 소스(명명 읽기쿼리 ID) — C# FieldDefinition.OptionsQueryId 미러. */
  optionsQueryId?: string | null
}

export interface GridColumnDefinition {
  key: string
  caption: string
  visible?: boolean
  width?: number | null   // 고정 폭(px, 미지정=자동) — 순서는 배열 순서(Phase-2)
}

export type LayoutNode =
  | SectionNode | RowNode | ColumnNode
  | GridWidget | FormWidget | FieldWidget | ButtonWidget | TextWidget | KpiWidget | BadgeWidget | TrendChartWidget

interface NodeBase {
  id?: string
  requiredPermission?: string | null
}
export interface SectionNode extends NodeBase { kind: 'section'; title?: string; children?: LayoutNode[] }
export interface RowNode     extends NodeBase { kind: 'row'; children?: LayoutNode[] }
export interface ColumnNode  extends NodeBase { kind: 'column'; span: number; children?: LayoutNode[] }
export interface GridWidget  extends NodeBase { kind: 'grid'; queryId?: string | null; columns?: GridColumnDefinition[] }
// isolated=true(Phase-2 멀티폼) — 폼 전용 모델 격리(폼별 저장/검증). 기본 false=화면 공유 모델(하위호환).
export interface FormWidget  extends NodeBase { kind: 'form'; saveQueryId?: string | null; fields?: FieldWidget[]; isolated?: boolean }
export interface FieldWidget extends NodeBase { kind: 'field'; fieldKey?: string | null; field?: FieldDefinition | null }
export interface ButtonWidget extends NodeBase { kind: 'commandButton'; label: string; command?: string | null; confirmMessage?: string | null }
export interface TextWidget  extends NodeBase { kind: 'text'; text: string; isLabel?: boolean }
// KPI 카드(Phase-2) — 바인딩 쿼리 첫 행의 valueColumn 값을 큰 숫자로 표시(C# KpiWidget 미러).
export interface KpiWidget   extends NodeBase { kind: 'kpi'; label: string; queryId?: string | null; valueColumn?: string | null; unit?: string | null; linkUiId?: string | null }
// 상태 뱃지(Phase-2) — 값→심각도 규칙 매칭(C# BadgeWidget/BadgeStyleRule 미러).
export interface BadgeStyleRule { value: string; severity: string; displayText?: string | null }
export interface BadgeWidget extends NodeBase { kind: 'statusBadge'; label?: string | null; queryId?: string | null; valueColumn?: string | null; styles?: BadgeStyleRule[] }
// 트렌드 차트(Phase-2 실시간 v2) — 마지막 maxPoints개 수치의 SVG 라인(C# TrendChartWidget 미러).
export interface TrendChartWidget extends NodeBase { kind: 'trendChart'; label: string; queryId?: string | null; valueColumn?: string | null; maxPoints?: number; timeColumn?: string | null }

export interface ScreenDefinitionDto {
  uiId: string
  title: string
  fields: FieldDefinition[]
  columns?: GridColumnDefinition[] | null
  queryId?: string | null
  saveQueryId?: string | null
  layout?: LayoutNode | null
  refreshIntervalSeconds?: number | null   // 자동 새로고침 주기(초, Phase-2 실시간 v2) — 저장 왕복 보존 필수
  searchFields?: FieldDefinition[] | null  // 검색 조건 영역(C# ScreenDefinition.SearchFields 미러) — 저장 왕복 보존 필수
}

export interface GrapesNode {
  type?: string
  attributes?: Record<string, unknown>
  components?: GrapesNode[]
}
