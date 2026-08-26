import { describe, it, expect, vi, beforeEach } from 'vitest'
import {
  createDefinition, entryPathFor, importScreenSeed, listDefinitions, listQueries, listScreenSeeds,
  loadDefinition, previewScreenSeed, saveDefinition,
} from '../api'
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
    fetchMock.mockReturnValueOnce(ok([{
      UI_ID: 'X', TITLE: '로드됨', DEFINITION_JSON: defJson,
      TARGET_CHANNEL: 'MOBILE', ENTRY_PATH: '/Mobile/X',
    }]))
    const res = await loadDefinition('X')
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toContain('/api/v1/query/SYS.GetScreenDefinition')
    expect(JSON.parse(init.body)).toEqual({ uiId: 'X' })
    expect(res.title).toBe('로드됨')
    expect(res.layout).toEqual(layout)
    expect(res.targetChannel).toBe('MOBILE')
    expect(res.entryPath).toBe('/Mobile/X')
    expect(res.source).toBe('database')
    expect(res.canonicalUiId).toBe('X')
  })

  it('loadDefinition은 DB와 코드 시드가 모두 없으면 저장 불가 missing 상태를 반환', async () => {
    fetchMock.mockReturnValueOnce(ok([]))
    fetchMock.mockReturnValueOnce(Promise.resolve(new Response('', { status: 404 })))
    const res = await loadDefinition('NEW')
    expect(res).toEqual({
      canonicalUiId: 'NEW',
      title: '', layout: null, flat: null,
      targetChannel: 'MES', entryPath: '/meta/NEW',
      source: 'missing', diagnostics: [], canImport: false,
    })
  })

  it('loadDefinition은 DB가 없을 때 코드 시드 원본을 읽기 전용 미리보기로 로드', async () => {
    const defJson = JSON.stringify({ uiId: 'SEED', title: '코드 화면', purpose: 'Auto', fields: [], layout })
    fetchMock
      .mockReturnValueOnce(ok([]))
      .mockReturnValueOnce(ok({
        uiId: 'SEED', title: '코드 화면', purpose: 'Auto', databaseExists: false, canImport: true,
        targetChannel: 'POP', entryPath: '/POP/SEED',
        definitionJson: defJson,
        diagnostics: [{ uiId: 'SEED', purpose: 'Auto', code: 'META-CAP-000', severity: 'Advisory', message: '목적 확인' }],
      }))

    const res = await loadDefinition('SEED_ALIAS')

    expect(fetchMock.mock.calls[1][0]).toContain('/api/v1/sys/screen-seeds/SEED_ALIAS')
    expect(res).toMatchObject({
      canonicalUiId: 'SEED', title: '코드 화면', layout, source: 'seed', canImport: true,
      targetChannel: 'POP', entryPath: '/POP/SEED',
    })
    expect(res.diagnostics).toEqual([
      { uiId: 'SEED', purpose: 'Auto', code: 'META-CAP-000', severity: 'Advisory', message: '목적 확인' },
    ])
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
    expect(JSON.parse(body.definitionJson).purpose).toBe('Auto')
    expect(body.targetChannel).toBe('MES')
    expect(body.entryPath).toBe('/meta/X')
    expect(affected).toBe(1)
  })

  it('saveDefinition은 로드한 화면 목적을 extras에서 definitionJson으로 보존한다', async () => {
    fetchMock.mockReturnValueOnce(ok({ affected: 1 }))

    await saveDefinition('M1', '관리', layout, { purpose: 'Manage' })

    const body = JSON.parse(fetchMock.mock.calls[0][1].body)
    expect(JSON.parse(body.definitionJson).purpose).toBe('Manage')
  })

  it('listDefinitions는 DB 메타를 정규화하고 레거시 행은 MES로 폴백', async () => {
    fetchMock.mockReturnValueOnce(ok([
      { UI_ID: 'M1', TITLE: '모바일', TARGET_CHANNEL: 'MOBILE', ENTRY_PATH: '/Mobile/M1', UPDATED_AT: '2026-07-10' },
      { UI_ID: 'OLD', TITLE: '레거시' },
    ]))

    const rows = await listDefinitions()
    expect(JSON.parse(fetchMock.mock.calls[0][1].body)).toEqual({})
    expect(rows).toEqual([
      { uiId: 'M1', title: '모바일', targetChannel: 'MOBILE', entryPath: '/Mobile/M1', updatedAt: '2026-07-10' },
      { uiId: 'OLD', title: '레거시', targetChannel: 'MES', entryPath: '/meta/OLD', updatedAt: null },
    ])
  })

  it('코드 시드 목록/상세를 정규화하고 오류 진단은 가져오기 불가로 유지', async () => {
    fetchMock.mockReturnValueOnce(ok([{
      uiId: 'BAD', title: '검증 오류', purpose: 'Manage', databaseExists: false, canImport: false,
      diagnostics: [{ uiId: 'BAD', purpose: 'Manage', code: 'META-CAP-101', severity: 'Error', message: '입력 필요' }],
    }]))

    const rows = await listScreenSeeds()

    expect(fetchMock.mock.calls[0][0]).toContain('/api/v1/sys/screen-seeds')
    expect(fetchMock.mock.calls[0][1].method).toBe('GET')
    expect(rows[0]).toMatchObject({
      uiId: 'BAD', purpose: 'Manage', databaseExists: false, canImport: false,
      errorCount: 1, advisoryCount: 0, targetChannel: 'MES', entryPath: '/meta/BAD',
    })

    fetchMock.mockReturnValueOnce(ok({
      uiId: 'BAD', title: '검증 오류', purpose: 'Manage', definitionJson: '{}', canImport: false,
      targetChannel: 'MOBILE', entryPath: '/Mobile/BAD',
    }))
    expect(await previewScreenSeed('BAD')).toMatchObject({
      definitionJson: '{}', targetChannel: 'MOBILE', entryPath: '/Mobile/BAD',
    })
  })

  it('importScreenSeed는 인코딩한 경로에 본문 없이 POST', async () => {
    fetchMock.mockReturnValueOnce(ok({ uiId: 'SEED 1' }))

    await importScreenSeed('SEED 1')

    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toContain('/api/v1/sys/screen-seeds/SEED%201/import')
    expect(init.method).toBe('POST')
    expect(init.body).toBeUndefined()
  })

  it('createDefinition은 선택 채널과 완전한 런타임 경로를 DB command에 보낸다', async () => {
    fetchMock.mockReturnValueOnce(ok({ affected: 1 }))
    await createDefinition('POP_WORK', 'POP 작업', 'POP')

    const body = JSON.parse(fetchMock.mock.calls[0][1].body)
    expect(body.targetChannel).toBe('POP')
    expect(body.entryPath).toBe('/POP/POP_WORK')
    expect(entryPathFor('MOBILE', 'MOB 1')).toBe('/Mobile/MOB%201')
  })

  it('listQueries는 카탈로그를 read/write로 분리', async () => {
    fetchMock.mockReturnValueOnce(ok([
      { id: 'MDM.PlantList', isWrite: false, requiredPermission: 'mdm:read', source: 'NamedQuery' },
      {
        id: 'bridge:mdm.create-plant', isWrite: true, requiredPermission: 'mdm:manage',
        source: 'BridgeCommand', effect: 'Mutating', executionMode: 'PerRow',
      },
    ]))
    const { reads, writes } = await listQueries()
    expect(reads).toEqual([
      { id: 'MDM.PlantList', isWrite: false, requiredPermission: 'mdm:read', source: 'NamedQuery' },
    ])
    expect(writes).toEqual([{
      id: 'bridge:mdm.create-plant', isWrite: true, requiredPermission: 'mdm:manage',
      source: 'BridgeCommand', effect: 'Mutating', executionMode: 'PerRow',
    }])
  })
})
