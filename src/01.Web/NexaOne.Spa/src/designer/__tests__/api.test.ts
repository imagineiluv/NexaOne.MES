import { describe, it, expect, vi, beforeEach } from 'vitest'
import { loadDefinition, saveDefinition, listQueries } from '../api'
import type { LayoutNode } from '../layout'

const fetchMock = vi.fn()
beforeEach(() => {
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})
function ok(body: unknown) {
  return Promise.resolve(new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } }))
}

const layout: LayoutNode = { kind: 'section', id: 's', children: [] }

describe('디자이너 API 클라이언트', () => {
  it('loadDefinition은 query 엔드포인트를 호출하고 DEFINITION_JSON을 파싱', async () => {
    const defJson = JSON.stringify({ uiId: 'X', title: '로드됨', fields: [], layout })
    fetchMock.mockReturnValueOnce(ok([{ UI_ID: 'X', TITLE: '로드됨', DEFINITION_JSON: defJson }]))
    const res = await loadDefinition('X')
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toContain('/api/v1/query/SYS.GetScreenDefinition')
    expect(JSON.parse(init.body)).toEqual({ uiId: 'X' })
    expect(res.title).toBe('로드됨')
    expect(res.layout).toEqual(layout)
  })

  it('loadDefinition은 빈 결과(신규 화면)에 layout=null', async () => {
    fetchMock.mockReturnValueOnce(ok([]))
    const res = await loadDefinition('NEW')
    expect(res).toEqual({ title: '', layout: null })
  })

  it('saveDefinition은 command 엔드포인트에 {uiId,title,definitionJson}을 보낸다', async () => {
    fetchMock.mockReturnValueOnce(ok({ affected: 1 }))
    const affected = await saveDefinition('X', '저장', layout)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toContain('/api/v1/command/SYS.UpsertScreenDefinition')
    const body = JSON.parse(init.body)
    expect(body.uiId).toBe('X')
    expect(body.title).toBe('저장')
    expect(typeof body.definitionJson).toBe('string')
    expect(JSON.parse(body.definitionJson).layout).toEqual(layout)
    expect(affected).toBe(1)
  })

  it('listQueries는 카탈로그를 read/write로 분리', async () => {
    fetchMock.mockReturnValueOnce(ok([
      { id: 'MDM.PlantList', isWrite: false, requiredPermission: null },
      { id: 'MDM.CreatePlant', isWrite: true, requiredPermission: 'mdm:manage' },
    ]))
    const { reads, writes } = await listQueries()
    expect(reads).toEqual(['MDM.PlantList'])
    expect(writes).toEqual([{ id: 'MDM.CreatePlant', requiredPermission: 'mdm:manage' }])
  })
})
