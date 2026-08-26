import { describe, it, expect } from 'vitest'
import {
  BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildEditorConfig, buildTraitDefs, toModelDefaults,
  requiredPermissionForBinding, syncRequiredPermission,
} from '../grapesConfig'
import type { PermissionSyncComponent, QueryCatalog } from '../grapesConfig'

const READ = (id: string, requiredPermission: string | null = null) => ({
  id, isWrite: false, requiredPermission,
})
const WRITE = (id: string, requiredPermission: string | null = null) => ({
  id, isWrite: true, requiredPermission,
})

const TYPES = ['nx-badge-widget', 'nx-button', 'nx-collection', 'nx-column', 'nx-field', 'nx-form', 'nx-grid', 'nx-kpi', 'nx-row', 'nx-section', 'nx-text', 'nx-trend-chart']

describe('GrapesJS 디자이너 설정(잠금)', () => {
  it('12개 블록만 노출(반복 항목 + §5 컴포넌트 세트 + Phase-2 위젯)', () => {
    expect(BLOCK_DEFS.map(b => b.id).sort()).toEqual([...TYPES].sort())
  })

  it('12개 컴포넌트 type 정의', () => {
    expect(COMPONENT_TYPE_DEFS.map(c => c.type).sort()).toEqual([...TYPES].sort())
  })

  it('init 설정은 스타일·기본 블록·기본 패널을 잠근다(스타일/코드/RTE UI 비노출)', () => {
    const cfg = buildEditorConfig(document.createElement('div'))
    expect(cfg.storageManager).toBe(false)
    expect(cfg.blockManager.blocks).toEqual([])
    expect(cfg.styleManager.sectors).toEqual([])
    expect(cfg.panels.defaults).toEqual([])
  })

  it('블록·트레이트 매니저는 전용 컨테이너(appendTo)에 마운트', () => {
    const c = document.createElement('div')
    const b = document.createElement('div')
    const t = document.createElement('div')
    const cfg = buildEditorConfig(c, b, t)
    expect(cfg.blockManager.appendTo).toBe(b)
    expect(cfg.traitManager.appendTo).toBe(t)
  })

  it('캔버스는 빈 드롭 영역과 위젯 프리뷰를 제공하고 블록을 목적별 카테고리로 구분', () => {
    const cfg = buildEditorConfig(document.createElement('div'))
    expect(cfg.canvas.frameStyle).toContain('[data-nx-component="nx-section"]:empty::after')
    expect(cfg.canvas.frameStyle).toContain('[data-nx-component="nx-grid"]')
    expect(cfg.canvas.frameStyle).toContain(':has(> [data-nx-component="nx-button"])')
    expect(cfg.canvas.frameStyle).toContain('prefers-reduced-motion')
    for (const mode of ['standard', 'dense', 'cards', 'split']) {
      expect(cfg.canvas.frameStyle).toContain(`html[data-nx-manage-preview="${mode}"]`)
    }
    expect(new Set(BLOCK_DEFS.map(block => block.category))).toEqual(new Set([
      '1. 레이아웃', '2. 입력·실행', '3. 데이터 표현',
    ]))
    expect(BLOCK_DEFS.every(block => block.description.length > 0)).toBe(true)
  })

  it('중첩 규칙(선언): POM 상세의 중첩 section과 command row를 포함하고 section은 row만 담는다', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const row = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-row')!
    const button = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-button')!
    const section = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-section')!
    expect(col.allowedChildren).toContain('nx-grid')
    expect(col.allowedChildren).toContain('nx-collection')
    expect(col.allowedChildren).toContain('nx-section')
    expect(row.allowedChildren).toContain('nx-button')
    expect(button.allowedParents).toContain('nx-row')
    expect(section.allowedChildren).toEqual(['nx-row'])
  })

  it('collection은 field만 담고 field는 form과 collection 양쪽에 배치할 수 있다', () => {
    const collection = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-collection')!
    const field = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-field')!
    expect(collection.allowedChildren).toEqual(['nx-field'])
    expect(collection.allowedParents).toEqual(['nx-column'])
    expect(field.allowedParents).toEqual(['nx-form', 'nx-collection'])
  })

  it('toModelDefaults: droppable 함수가 허용 type만 수락(문자열 셀렉터 아님)', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const defaults = toModelDefaults(col, [])
    expect(typeof defaults.droppable).toBe('function')
    const drop = defaults.droppable as (s: { is(t: string): boolean }) => boolean
    expect(drop({ is: t => t === 'nx-grid' })).toBe(true)
    expect(drop({ is: t => t === 'nx-section' })).toBe(true)
    expect(drop({ is: t => t === 'nx-row' })).toBe(false)
    const grid = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-grid')!
    expect(toModelDefaults(grid, []).droppable).toBe(false)
  })

  it('toModelDefaults: draggable 함수가 허용 부모만 수락하고 중첩 section은 column만 수락', () => {
    const row = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-row')!
    const drag = toModelDefaults(row, []).draggable as (s: unknown, t: { is(x: string): boolean }) => boolean
    expect(drag(null, { is: t => t === 'nx-section' })).toBe(true)
    expect(drag(null, { is: t => t === 'nx-column' })).toBe(false)
    const section = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-section')!
    const sectionDrag = toModelDefaults(section, []).draggable as (s: unknown, t: { is(x: string): boolean }) => boolean
    expect(sectionDrag(null, { is: t => t === 'nx-column' })).toBe(true)
    expect(sectionDrag(null, { is: t => t === 'nx-row' })).toBe(false)
  })

  it('toModelDefaults는 트레이트를 그대로 전달', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const traits = buildTraitDefs({ reads: [], writes: [] })['nx-column']
    expect(toModelDefaults(col, traits).traits).toBe(traits)
  })

  it('buildTraitDefs는 grid에 read 쿼리 드롭다운, button에 write 쿼리 드롭다운', () => {
    const traits = buildTraitDefs({ reads: [READ('MDM.PlantList', 'mdm:read')], writes: [WRITE('MDM.CreatePlant', 'mdm:manage')] })
    const gridQuery = traits['nx-grid'].find(t => t.name === 'data-query-id')!
    expect(gridQuery.options!.map(o => o.id)).toContain('MDM.PlantList')
    const btnCmd = traits['nx-button'].find(t => t.name === 'data-command')!
    expect(btnCmd.options!.map(o => o.id)).toContain('MDM.CreatePlant')
  })

  it('buildTraitDefs는 typed bridge 작업지시 액션을 명령 버튼에서 보존한다', () => {
    const action = 'bridge:pom.work-order.start'
    const traits = buildTraitDefs({
      reads: [],
      writes: [WRITE(action, 'pom:execute')],
    })

    const btnCmd = traits['nx-button'].find(t => t.name === 'data-command')!
    expect(btnCmd.options!.map(o => o.id)).toContain(action)
  })

  it('buildTraitDefs는 nx-field에 숨김·자동 생성까지 이산 트레이트로 노출', () => {
    const traits = buildTraitDefs({ reads: [], writes: [] })
    const field = traits['nx-field']
    expect(field.map(t => t.name)).toEqual([
      'data-field-key', 'data-field-label', 'data-field-type',
      'data-field-required', 'data-field-readonly', 'data-field-hidden', 'data-field-value-generator', 'data-field-options',
      'data-field-options-query',
    ])
    const byName = (n: string) => field.find(t => t.name === n)!
    expect(byName('data-field-type').type).toBe('select')
    expect(byName('data-field-required').type).toBe('checkbox')
    expect(byName('data-field-readonly').type).toBe('checkbox')
    expect(byName('data-field-hidden').type).toBe('checkbox')
    expect(byName('data-field-value-generator').options!.map(option => option.id)).toEqual(['None', 'UuidV4'])
    expect(byName('data-field-label').type).toBe('text')
  })

  it('data-field-type 셀렉트 옵션은 정확히 5개 FieldType', () => {
    const field = buildTraitDefs({ reads: [], writes: [] })['nx-field']
    const typeTrait = field.find(t => t.name === 'data-field-type')!
    expect(typeTrait.options!.map(o => o.id)).toEqual(['Text', 'Number', 'Boolean', 'Date', 'Select'])
  })

  it('buildTraitDefs는 nx-grid에 컬럼 작성 트레이트(data-columns, JSON)를 쿼리 트레이트와 함께 노출', () => {
    const grid = buildTraitDefs({ reads: [READ('Q')], writes: [] })['nx-grid']
    expect(grid.map(t => t.name)).toContain('data-query-id')
    // 구 data-columns-spec(콤마/콜론 spec)은 제거 — 구분자 무손실 JSON 트레이트로 대체.
    expect(grid.map(t => t.name)).not.toContain('data-columns-spec')
    const colTrait = grid.find(t => t.name === 'data-columns')!
    expect(colTrait).toBeDefined()
    expect(colTrait.type).toBe('text')
  })

  it('buildTraitDefs는 nx-field에 옵션 트레이트(data-field-options)를 여전히 노출', () => {
    const field = buildTraitDefs({ reads: [], writes: [] })['nx-field']
    const optTrait = field.find(t => t.name === 'data-field-options')!
    expect(optTrait).toBeDefined()
    expect(optTrait.type).toBe('text')
  })

  it('collection trait은 모델 키·라벨·최소/최대 항목을 모두 편집한다', () => {
    const names = buildTraitDefs({ reads: [], writes: [] })['nx-collection'].map(trait => trait.name)
    expect(names).toEqual([
      'data-collection-key', 'data-binding-scope', 'data-label', 'data-item-label', 'data-min-items', 'data-max-items',
    ])
  })

  it('binding 선택·교체·해제는 requiredPermission을 동기화하고 stale 값을 제거한다', () => {
    const queries: QueryCatalog = {
      reads: [READ('MDM.PlantList', 'mdm:read'), READ('SYS.PublicCodes')],
      writes: [WRITE('MDM.CreatePlant', 'mdm:manage')],
    }
    let attributes: Record<string, unknown> = {
      'data-query-id': 'MDM.PlantList',
      'data-required-permission': 'old:permission',
    }
    const component: PermissionSyncComponent = {
      is: type => type === 'nx-grid',
      getAttributes: () => attributes,
      addAttributes: next => { attributes = { ...attributes, ...next } },
      removeAttributes: value => {
        for (const key of Array.isArray(value) ? value : [value]) delete attributes[key]
      },
    }

    expect(syncRequiredPermission(component, queries)).toBe(true)
    expect(attributes['data-required-permission']).toBe('mdm:read')

    attributes['data-query-id'] = 'SYS.PublicCodes'
    expect(requiredPermissionForBinding(component, queries)).toBeNull()
    expect(syncRequiredPermission(component, queries)).toBe(true)
    expect(attributes).not.toHaveProperty('data-required-permission')

    attributes['data-query-id'] = 'REMOVED.Query'
    attributes['data-required-permission'] = 'mdm:read'
    expect(syncRequiredPermission(component, queries)).toBe(true)
    expect(attributes).not.toHaveProperty('data-required-permission')
  })

  it('write binding은 write descriptor 권한을 사용하고 비바인딩 노드는 수정하지 않는다', () => {
    const queries: QueryCatalog = { reads: [], writes: [WRITE('QMS.Approve', 'qms:manage')] }
    let buttonAttributes: Record<string, unknown> = { 'data-command': 'QMS.Approve' }
    const button: PermissionSyncComponent = {
      is: type => type === 'nx-button',
      getAttributes: () => buttonAttributes,
      addAttributes: next => { buttonAttributes = { ...buttonAttributes, ...next } },
      removeAttributes: value => {
        for (const key of Array.isArray(value) ? value : [value]) delete buttonAttributes[key]
      },
    }
    expect(syncRequiredPermission(button, queries)).toBe(true)
    expect(buttonAttributes['data-required-permission']).toBe('qms:manage')

    let textAttributes: Record<string, unknown> = { 'data-required-permission': 'custom:read' }
    const text: PermissionSyncComponent = {
      is: type => type === 'nx-text',
      getAttributes: () => textAttributes,
      addAttributes: next => { textAttributes = { ...textAttributes, ...next } },
      removeAttributes: value => {
        for (const key of Array.isArray(value) ? value : [value]) delete textAttributes[key]
      },
    }
    expect(syncRequiredPermission(text, queries)).toBe(false)
    expect(textAttributes['data-required-permission']).toBe('custom:read')
  })
})
