// C# ScreenDefinition/LayoutNode(§5)의 TS 미러. 직렬화 형식의 권위는 C# ScreenDefinitionJson이며
// 여기서는 그 camelCase 형태를 타깃으로 한다(병렬 직렬화기 금지 — mapping.ts는 GrapesJS↔스키마 변환만).
export type FieldType = 'Text' | 'Number' | 'Boolean' | 'Date' | 'Select'
export const FIELD_VALUE_GENERATOR_VALUES = ['None', 'UuidV4'] as const
export type FieldValueGenerator = typeof FIELD_VALUE_GENERATOR_VALUES[number]
export const SCREEN_PURPOSE_VALUES = ['Auto', 'Register', 'Manage', 'Inquiry', 'Report', 'Execute'] as const
export type ScreenPurpose = typeof SCREEN_PURPOSE_VALUES[number]

/** 서버 QueryCatalogController descriptor. 확장 필드는 구버전 서버와의 호환을 위해 선택적이다. */
export interface QueryDescriptor {
  id: string
  isWrite: boolean
  requiredPermission: string | null
  source?: 'NamedQuery' | 'BridgeCommand'
  effect?: 'Mutating' | 'NonMutating'
  executionMode?: 'PerRow' | 'HostRequiredAggregate'
}

/** read/write 모두 descriptor를 보존해야 바인딩 변경 시 권한을 자동 동기화할 수 있다. */
export interface QueryCatalog { reads: QueryDescriptor[]; writes: QueryDescriptor[] }

export interface FieldDefinition {
  key: string
  label: string
  type: FieldType
  required?: boolean
  readOnly?: boolean
  options?: string[] | null
  /** Select 옵션의 동적 소스(명명 읽기쿼리 ID) — C# FieldDefinition.OptionsQueryId 미러. */
  optionsQueryId?: string | null
  /** 런타임 입력에는 노출하지 않되 명령 모델에는 유지하는 시스템 필드. */
  hidden?: boolean
  /** 신규 등록 모델을 준비할 때 빈 값에 한 번만 적용하는 생성 규칙. */
  valueGenerator?: FieldValueGenerator
}

export interface GridColumnDefinition {
  key: string
  caption: string
  visible?: boolean
  width?: number | null   // 고정 폭(px, 미지정=자동) — 순서는 배열 순서(Phase-2)
}

export type LayoutNode =
  | SectionNode | RowNode | ColumnNode
  | GridWidget | FormWidget | FieldWidget | CollectionWidget | ButtonWidget | TextWidget | KpiWidget | BadgeWidget | TrendChartWidget

interface NodeBase {
  id?: string
  requiredPermission?: string | null
}
export interface SectionNode extends NodeBase { kind: 'section'; title?: string; children?: LayoutNode[] }
export interface RowNode     extends NodeBase { kind: 'row'; children?: LayoutNode[] }
export interface ColumnNode  extends NodeBase { kind: 'column'; span: number; children?: LayoutNode[] }
export interface GridWidget  extends NodeBase {
  kind: 'grid'
  queryId?: string | null
  columns?: GridColumnDefinition[]
  // 선택 행 일괄 명령(C# BulkCommandDefinition 미러) — 저장 왕복 보존 필수.
  bulkCommands?: BulkCommandDefinition[] | null
  selectionScope?: string | null
  selectionDisabled?: boolean
}
// isolated=true(Phase-2 멀티폼) — 폼 전용 모델 격리(폼별 저장/검증). 기본 false=화면 공유 모델(하위호환).
export interface FormWidget  extends NodeBase { kind: 'form'; saveQueryId?: string | null; fields?: FieldWidget[]; isolated?: boolean; bindingScope?: string | null }
export interface FieldWidget extends NodeBase { kind: 'field'; fieldKey?: string | null; field?: FieldDefinition | null }
/** 화면 공유 모델의 collectionKey에 동일 필드 스키마의 반복 항목을 보관한다. */
export interface CollectionWidget extends NodeBase {
  kind: 'collection'
  collectionKey: string
  label: string
  itemLabel: string
  fields?: FieldWidget[]
  bindingScope?: string | null
  minItems?: number
  maxItems?: number | null
}
export interface ButtonWidget extends NodeBase { kind: 'commandButton'; label: string; command?: string | null; confirmMessage?: string | null; bindingScope?: string | null }
export interface TextWidget  extends NodeBase { kind: 'text'; text: string; isLabel?: boolean }
// KPI 카드(Phase-2) — 바인딩 쿼리 첫 행의 valueColumn 값을 큰 숫자로 표시(C# KpiWidget 미러).
export interface KpiWidget   extends NodeBase { kind: 'kpi'; label: string; queryId?: string | null; valueColumn?: string | null; unit?: string | null; linkUiId?: string | null }
// 상태 뱃지(Phase-2) — 값→심각도 규칙 매칭(C# BadgeWidget/BadgeStyleRule 미러).
export interface BadgeStyleRule { value: string; severity: string; displayText?: string | null }
export interface BadgeWidget extends NodeBase { kind: 'statusBadge'; label?: string | null; queryId?: string | null; valueColumn?: string | null; styles?: BadgeStyleRule[] }
// 트렌드 차트(Phase-2 실시간 v2) — 마지막 maxPoints개 수치의 SVG 라인(C# TrendChartWidget 미러).
// valueColumns(P3-13 v2): 다중 시리즈 컬럼(콤마 구분 data 속성). 지정 시 valueColumn보다 우선.
export interface TrendChartWidget extends NodeBase { kind: 'trendChart'; label: string; queryId?: string | null; valueColumn?: string | null; valueColumns?: string[] | null; maxPoints?: number; timeColumn?: string | null }

// 그리드 일괄 명령(C# BulkCommandDefinition 미러) — 선택 행 상태전이 커맨드. 저장 왕복 보존 필수.
export interface BulkCommandDefinition {
  label: string
  commandQueryId: string
  confirmMessage?: string | null
  requiredPermission?: string | null
}

export interface ScreenDefinitionDto {
  uiId: string
  title: string
  purpose: ScreenPurpose
  fields: FieldDefinition[]
  columns?: GridColumnDefinition[] | null
  queryId?: string | null
  saveQueryId?: string | null
  layout?: LayoutNode | null
  refreshIntervalSeconds?: number | null   // 자동 새로고침 주기(초, Phase-2 실시간 v2) — 저장 왕복 보존 필수
  searchFields?: FieldDefinition[] | null  // 검색 조건 영역(C# ScreenDefinition.SearchFields 미러) — 저장 왕복 보존 필수
  countQueryId?: string | null             // 서버측 페이징 count 쿼리(P3-9 v2, C# CountQueryId 미러) — 저장 왕복 보존 필수
  deleteQueryId?: string | null            // 그리드 표준 삭제(C# DeleteQueryId 미러) — 저장 왕복 보존 필수
  bulkCommands?: BulkCommandDefinition[] | null // 그리드 일괄 명령 — 저장 왕복 보존 필수
  readRequiredPermission?: string | null   // 평면 조회 바인딩 권한 힌트 — 서버 카탈로그가 최종 원천
  saveRequiredPermission?: string | null   // 평면 저장 바인딩 권한 힌트
  deleteRequiredPermission?: string | null // 평면 삭제 바인딩 권한 힌트
}

// ── TS↔C# 계약 드리프트 가드 ─────────────────────────────────────────────────
// 인터페이스는 런타임 소거되므로 vitest 대조용 키 상수를 유지한다(공유 픽스처 test/contract/*.json과 대조).
// 타입 완전성 체크: DTO에 키를 추가하고 여기(또는 반대)를 빠뜨리면 아래 상수가 컴파일 오류(tsc)로 잡는다.
export const SCREEN_DEFINITION_KEYS = [
  'uiId', 'title', 'purpose', 'fields', 'columns', 'queryId', 'saveQueryId', 'layout',
  'refreshIntervalSeconds', 'searchFields', 'countQueryId', 'deleteQueryId', 'bulkCommands',
  'readRequiredPermission', 'saveRequiredPermission', 'deleteRequiredPermission',
] as const
type _MissingScreenKeys = Exclude<keyof ScreenDefinitionDto, typeof SCREEN_DEFINITION_KEYS[number]>
type _ExtraScreenKeys = Exclude<typeof SCREEN_DEFINITION_KEYS[number], keyof ScreenDefinitionDto>
const _screenKeysExhaustive: _MissingScreenKeys | _ExtraScreenKeys extends never ? true : never = true
void _screenKeysExhaustive

export const BULK_COMMAND_KEYS = ['label', 'commandQueryId', 'confirmMessage', 'requiredPermission'] as const
type _MissingBulkKeys = Exclude<keyof BulkCommandDefinition, typeof BULK_COMMAND_KEYS[number]>
type _ExtraBulkKeys = Exclude<typeof BULK_COMMAND_KEYS[number], keyof BulkCommandDefinition>
const _bulkKeysExhaustive: _MissingBulkKeys | _ExtraBulkKeys extends never ? true : never = true
void _bulkKeysExhaustive

export const FIELD_DEFINITION_KEYS = [
  'key', 'label', 'type', 'required', 'readOnly', 'options', 'optionsQueryId', 'hidden', 'valueGenerator',
] as const
type _MissingFieldKeys = Exclude<keyof FieldDefinition, typeof FIELD_DEFINITION_KEYS[number]>
type _ExtraFieldKeys = Exclude<typeof FIELD_DEFINITION_KEYS[number], keyof FieldDefinition>
const _fieldKeysExhaustive: _MissingFieldKeys | _ExtraFieldKeys extends never ? true : never = true
void _fieldKeysExhaustive

export const GRID_WIDGET_KEYS = ['queryId', 'columns', 'bulkCommands', 'selectionScope', 'selectionDisabled'] as const
type _MissingGridKeys = Exclude<keyof Omit<GridWidget, keyof NodeBase | 'kind'>, typeof GRID_WIDGET_KEYS[number]>
type _ExtraGridKeys = Exclude<typeof GRID_WIDGET_KEYS[number], keyof GridWidget>
const _gridKeysExhaustive: _MissingGridKeys | _ExtraGridKeys extends never ? true : never = true
void _gridKeysExhaustive

export const FORM_WIDGET_KEYS = ['saveQueryId', 'fields', 'isolated', 'bindingScope'] as const
type _MissingFormKeys = Exclude<keyof Omit<FormWidget, keyof NodeBase | 'kind'>, typeof FORM_WIDGET_KEYS[number]>
type _ExtraFormKeys = Exclude<typeof FORM_WIDGET_KEYS[number], keyof FormWidget>
const _formKeysExhaustive: _MissingFormKeys | _ExtraFormKeys extends never ? true : never = true
void _formKeysExhaustive

export const COLLECTION_WIDGET_KEYS = ['collectionKey', 'label', 'itemLabel', 'fields', 'bindingScope', 'minItems', 'maxItems'] as const
type _MissingCollectionKeys = Exclude<keyof Omit<CollectionWidget, keyof NodeBase | 'kind'>, typeof COLLECTION_WIDGET_KEYS[number]>
type _ExtraCollectionKeys = Exclude<typeof COLLECTION_WIDGET_KEYS[number], keyof CollectionWidget>
const _collectionKeysExhaustive: _MissingCollectionKeys | _ExtraCollectionKeys extends never ? true : never = true
void _collectionKeysExhaustive

export const BUTTON_WIDGET_KEYS = ['label', 'command', 'confirmMessage', 'bindingScope'] as const
type _MissingButtonKeys = Exclude<keyof Omit<ButtonWidget, keyof NodeBase | 'kind'>, typeof BUTTON_WIDGET_KEYS[number]>
type _ExtraButtonKeys = Exclude<typeof BUTTON_WIDGET_KEYS[number], keyof ButtonWidget>
const _buttonKeysExhaustive: _MissingButtonKeys | _ExtraButtonKeys extends never ? true : never = true
void _buttonKeysExhaustive

/** C# JsonDerivedType discriminator와 공유 fixture가 같은 종류를 유지하도록 하는 런타임 목록. */
export const LAYOUT_KIND_VALUES = [
  'section', 'row', 'column', 'grid', 'form', 'field', 'collection', 'commandButton',
  'text', 'kpi', 'statusBadge', 'trendChart',
] as const satisfies readonly LayoutNode['kind'][]

export interface GrapesNode {
  type?: string
  attributes?: Record<string, unknown>
  components?: GrapesNode[]
}
