// 디자이너 ↔ Phase 5a 게이트웨이 클라이언트. 화면정의 로드/저장은 SYS 쿼리/커맨드(SQL 열은 대문자),
// 카탈로그(/api/v1/sys/queries)는 camelCase. apiFetch가 Bearer 부착·401 refresh·!ok throw를 처리.
import { apiFetch } from '../api/client'
import type { LayoutNode } from './layout'
import { buildDefinitionJson, parseDefinition } from './mapping'

interface ScreenDefRow { UI_ID?: string; TITLE?: string; DEFINITION_JSON?: string }
interface AffectedRows { affected: number }
interface QueryDescriptor { id: string; isWrite: boolean; requiredPermission: string | null }

export async function loadDefinition(uiId: string): Promise<{ title: string; layout: LayoutNode | null }> {
  const rows = await apiFetch<ScreenDefRow[]>('/api/v1/query/SYS.GetScreenDefinition', {
    method: 'POST',
    body: JSON.stringify({ uiId }),
  })
  const json = rows[0]?.DEFINITION_JSON
  if (!json) return { title: rows[0]?.TITLE ?? '', layout: null }
  return parseDefinition(json)
}

export async function saveDefinition(uiId: string, title: string, layout: LayoutNode | null): Promise<number> {
  const definitionJson = buildDefinitionJson(uiId, title, layout)
  const res = await apiFetch<AffectedRows>('/api/v1/command/SYS.UpsertScreenDefinition', {
    method: 'POST',
    body: JSON.stringify({ uiId, title, definitionJson }),
  })
  return res.affected
}

export async function listQueries(): Promise<{ reads: string[]; writes: { id: string; requiredPermission: string | null }[] }> {
  const items = await apiFetch<QueryDescriptor[]>('/api/v1/sys/queries', { method: 'GET' })
  return {
    reads: items.filter(q => !q.isWrite).map(q => q.id),
    writes: items.filter(q => q.isWrite).map(q => ({ id: q.id, requiredPermission: q.requiredPermission })),
  }
}
