// 디자이너 ↔ Phase 5a 게이트웨이 클라이언트. 화면정의 로드/저장은 SYS 쿼리/커맨드(SQL 열은 대문자),
// 카탈로그(/api/v1/sys/queries)는 camelCase. apiFetch가 Bearer 부착·401 refresh·!ok throw를 처리.
import { ApiError, apiFetch } from '../api/client'
import { SCREEN_PURPOSE_VALUES } from './layout'
import type {
  BulkCommandDefinition, FieldDefinition, LayoutNode, QueryCatalog, QueryDescriptor,
  ScreenDefinitionDto, ScreenPurpose,
} from './layout'
import { buildDefinitionJson, parseDefinition } from './mapping'

export const TARGET_CHANNELS = ['MES', 'MOBILE', 'POP'] as const
export type TargetChannel = typeof TARGET_CHANNELS[number]

interface ScreenDefRow {
  UI_ID?: string
  TITLE?: string
  DEFINITION_JSON?: string
  TARGET_CHANNEL?: string
  ENTRY_PATH?: string
  UPDATED_AT?: string
  uiId?: string
  title?: string
  definitionJson?: string
  targetChannel?: string
  entryPath?: string
  updatedAt?: string
}
interface AffectedRows { affected: number }
export interface ScreenDefinitionSummary {
  uiId: string
  title: string
  targetChannel: TargetChannel
  entryPath: string
  updatedAt: string | null
}

export type ScreenDefinitionSource = 'database' | 'seed' | 'missing'
export type ScreenCapabilityDiagnosticSeverity = 'Advisory' | 'Error'

export interface ScreenCapabilityDiagnostic {
  uiId: string
  purpose: ScreenPurpose
  code: string
  severity: ScreenCapabilityDiagnosticSeverity
  message: string
}

export interface ScreenSeedSummary {
  uiId: string
  title: string
  purpose: ScreenPurpose
  databaseExists: boolean
  canImport: boolean
  diagnostics: ScreenCapabilityDiagnostic[]
  errorCount: number
  advisoryCount: number
  targetChannel: TargetChannel
  entryPath: string
}

export interface ScreenSeedPreview extends ScreenSeedSummary {
  definitionJson: string
}

export interface LoadedScreenDefinition {
  canonicalUiId: string
  title: string
  layout: LayoutNode | null
  flat: ScreenDefinitionDto | null
  targetChannel: TargetChannel
  entryPath: string
  source: ScreenDefinitionSource
  diagnostics: ScreenCapabilityDiagnostic[]
  canImport: boolean
}

export function normalizeTargetChannel(value: unknown): TargetChannel {
  const normalized = typeof value === 'string' ? value.trim().toUpperCase() : ''
  return TARGET_CHANNELS.includes(normalized as TargetChannel) ? normalized as TargetChannel : 'MES'
}

export function entryPathFor(targetChannel: TargetChannel, uiId: string): string {
  const encoded = encodeURIComponent(uiId)
  if (targetChannel === 'MOBILE') return `/Mobile/${encoded}`
  if (targetChannel === 'POP') return `/POP/${encoded}`
  return `/meta/${encoded}`
}

function normalizeEntryPath(value: unknown, targetChannel: TargetChannel, uiId: string): string {
  return typeof value === 'string' && value.trim().startsWith('/')
    ? value.trim()
    : entryPathFor(targetChannel, uiId)
}

function summaryFromRow(row: ScreenDefRow): ScreenDefinitionSummary {
  const uiId = row.UI_ID ?? row.uiId ?? ''
  const targetChannel = normalizeTargetChannel(row.TARGET_CHANNEL ?? row.targetChannel)
  return {
    uiId,
    title: row.TITLE ?? row.title ?? uiId,
    targetChannel,
    entryPath: normalizeEntryPath(row.ENTRY_PATH ?? row.entryPath, targetChannel, uiId),
    updatedAt: row.UPDATED_AT ?? row.updatedAt ?? null,
  }
}

interface ScreenDiagnosticRaw {
  uiId?: string
  purpose?: string
  code?: string
  severity?: string | number
  message?: string
}

interface ScreenSeedRaw {
  uiId?: string
  title?: string
  purpose?: string
  databaseExists?: boolean
  canImport?: boolean
  definitionJson?: string
  targetChannel?: string
  entryPath?: string
  diagnostics?: ScreenDiagnosticRaw[]
  errorCount?: number
  advisoryCount?: number
}

function normalizePurpose(value: unknown): ScreenPurpose {
  return typeof value === 'string' && SCREEN_PURPOSE_VALUES.includes(value as ScreenPurpose)
    ? value as ScreenPurpose
    : 'Auto'
}

function normalizeDiagnostics(
  value: ScreenSeedRaw['diagnostics'], fallbackUiId: string, fallbackPurpose: ScreenPurpose,
): ScreenCapabilityDiagnostic[] {
  if (!Array.isArray(value)) return []
  return value.map(item => ({
    uiId: typeof item.uiId === 'string' ? item.uiId : fallbackUiId,
    purpose: normalizePurpose(item.purpose ?? fallbackPurpose),
    code: typeof item.code === 'string' ? item.code : '',
    severity: item.severity === 'Error' || item.severity === 1 ? 'Error' : 'Advisory',
    message: typeof item.message === 'string' ? item.message : '',
  }))
}

function seedSummaryFromRaw(raw: ScreenSeedRaw): ScreenSeedSummary {
  const uiId = typeof raw.uiId === 'string' ? raw.uiId : ''
  const purpose = normalizePurpose(raw.purpose)
  const targetChannel = normalizeTargetChannel(raw.targetChannel)
  const diagnostics = normalizeDiagnostics(raw.diagnostics, uiId, purpose)
  const databaseExists = raw.databaseExists === true
  const errorCount = typeof raw.errorCount === 'number'
    ? raw.errorCount
    : diagnostics.filter(item => item.severity === 'Error').length
  const advisoryCount = typeof raw.advisoryCount === 'number'
    ? raw.advisoryCount
    : diagnostics.filter(item => item.severity === 'Advisory').length
  return {
    uiId,
    title: typeof raw.title === 'string' && raw.title.length > 0 ? raw.title : uiId,
    purpose,
    databaseExists,
    canImport: typeof raw.canImport === 'boolean' ? raw.canImport : !databaseExists && errorCount === 0,
    diagnostics,
    errorCount,
    advisoryCount,
    targetChannel,
    entryPath: normalizeEntryPath(raw.entryPath, targetChannel, uiId),
  }
}

export async function listDefinitions(): Promise<ScreenDefinitionSummary[]> {
  const rows = await apiFetch<ScreenDefRow[]>('/api/v1/query/SYS.ListScreenDefinitions', {
    method: 'POST',
    body: JSON.stringify({}),
  })
  return rows.map(summaryFromRow).filter(row => row.uiId.length > 0)
}

/** 코드에 포함된 화면 시드와 DB 등록 여부/검증 결과를 조회한다. 정의 JSON은 상세 조회에서만 받는다. */
export async function listScreenSeeds(): Promise<ScreenSeedSummary[]> {
  const rows = await apiFetch<ScreenSeedRaw[]>('/api/v1/sys/screen-seeds', { method: 'GET' })
  return rows.map(seedSummaryFromRaw).filter(row => row.uiId.length > 0)
}

/** DB에 쓰기 전에 코드 시드 원본과 capability 진단을 미리 본다. */
export async function previewScreenSeed(uiId: string): Promise<ScreenSeedPreview> {
  const row = await apiFetch<ScreenSeedRaw>(`/api/v1/sys/screen-seeds/${encodeURIComponent(uiId)}`, { method: 'GET' })
  return {
    ...seedSummaryFromRaw(row),
    definitionJson: typeof row.definitionJson === 'string' ? row.definitionJson : '',
  }
}

/** 검증된 코드 시드를 insert-only 방식으로 DB에 가져온다. 요청 본문은 보내지 않는다. */
export async function importScreenSeed(uiId: string): Promise<void> {
  await apiFetch<unknown>(`/api/v1/sys/screen-seeds/${encodeURIComponent(uiId)}/import`, { method: 'POST' })
}

export async function loadDefinition(uiId: string): Promise<LoadedScreenDefinition> {
  const rows = await apiFetch<ScreenDefRow[]>('/api/v1/query/SYS.GetScreenDefinition', {
    method: 'POST',
    body: JSON.stringify({ uiId }),
  })
  const row = rows[0]
  const targetChannel = normalizeTargetChannel(row?.TARGET_CHANNEL ?? row?.targetChannel)
  const entryPath = normalizeEntryPath(row?.ENTRY_PATH ?? row?.entryPath, targetChannel, uiId)
  const json = row?.DEFINITION_JSON ?? row?.definitionJson
  if (row) {
    if (!json) {
      return {
        canonicalUiId: row.UI_ID ?? row.uiId ?? uiId,
        title: row.TITLE ?? row.title ?? '', layout: null, flat: null, targetChannel, entryPath,
        source: 'database', diagnostics: [], canImport: false,
      }
    }
    return {
      ...parseDefinition(json), canonicalUiId: row.UI_ID ?? row.uiId ?? uiId, targetChannel, entryPath,
      source: 'database', diagnostics: [], canImport: false,
    }
  }

  // DB가 비어 있을 때만 코드 시드를 조회한다. 404는 신규 화면으로 만들지 않고 명시적인 missing 상태로 반환해
  // 직접 URL 진입이 빈 정의를 실수로 DB에 덮어쓰지 않게 한다.
  try {
    const seed = await previewScreenSeed(uiId)
    return {
      ...parseDefinition(seed.definitionJson),
      canonicalUiId: seed.uiId,
      title: seed.title,
      targetChannel: seed.targetChannel,
      entryPath: seed.entryPath,
      source: 'seed',
      diagnostics: seed.diagnostics,
      canImport: seed.canImport,
    }
  } catch (error) {
    if (!(error instanceof ApiError) || error.status !== 404) throw error
    return {
      canonicalUiId: uiId,
      title: '', layout: null, flat: null, targetChannel: 'MES', entryPath: entryPathFor('MES', uiId),
      source: 'missing', diagnostics: [], canImport: false,
    }
  }
}

// extras = 디자이너가 편집하지 않는 화면 수준 설정 — 로드 시 flat에서 받아 재저장에 그대로 실어
// 드랍을 막는다(refreshIntervalSeconds가 이 배선 누락으로 재저장에서 소실되던 버그 교정).
export interface ScreenLevelExtras {
  purpose?: ScreenPurpose | null
  refreshIntervalSeconds?: number | null
  searchFields?: FieldDefinition[] | null
  countQueryId?: string | null
  deleteQueryId?: string | null
  bulkCommands?: BulkCommandDefinition[] | null
  readRequiredPermission?: string | null
  saveRequiredPermission?: string | null
  deleteRequiredPermission?: string | null
  targetChannel?: TargetChannel
  entryPath?: string
}

export async function saveDefinition(
  uiId: string, title: string, layout: LayoutNode | null, extras?: ScreenLevelExtras,
): Promise<number> {
  const definitionJson = buildDefinitionJson(
    uiId, title, layout, extras?.refreshIntervalSeconds, extras?.searchFields, extras?.countQueryId,
    extras?.deleteQueryId, extras?.bulkCommands, extras?.purpose,
    extras?.readRequiredPermission, extras?.saveRequiredPermission, extras?.deleteRequiredPermission)
  const targetChannel = extras?.targetChannel ?? 'MES'
  const entryPath = extras?.entryPath ?? entryPathFor(targetChannel, uiId)
  const res = await apiFetch<AffectedRows>('/api/v1/command/SYS.UpsertScreenDefinition', {
    method: 'POST',
    body: JSON.stringify({ uiId, title, definitionJson, targetChannel, entryPath }),
  })
  return res.affected
}

export async function createDefinition(uiId: string, title: string, targetChannel: TargetChannel): Promise<number> {
  return saveDefinition(uiId, title, null, { targetChannel, entryPath: entryPathFor(targetChannel, uiId) })
}

export async function listQueries(): Promise<QueryCatalog> {
  const items = await apiFetch<QueryDescriptor[]>('/api/v1/sys/queries', { method: 'GET' })
  return {
    reads: items.filter(q => !q.isWrite),
    writes: items.filter(q => q.isWrite),
  }
}
