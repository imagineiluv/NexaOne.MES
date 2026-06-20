import { describe, it, expect } from 'vitest'
import {
  BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildEditorConfig, buildTraitDefs, toModelDefaults,
} from '../grapesConfig'

const TYPES = ['nx-button', 'nx-column', 'nx-field', 'nx-form', 'nx-grid', 'nx-row', 'nx-section', 'nx-text']

describe('GrapesJS 디자이너 설정(잠금)', () => {
  it('8개 블록만 노출(§5 컴포넌트 세트)', () => {
    expect(BLOCK_DEFS.map(b => b.id).sort()).toEqual([...TYPES].sort())
  })

  it('8개 컴포넌트 type 정의', () => {
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

  it('중첩 규칙(선언): column은 위젯을 자식 허용, section은 row만', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const section = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-section')!
    expect(col.allowedChildren).toContain('nx-grid')
    expect(section.allowedChildren).toEqual(['nx-row'])
  })

  it('toModelDefaults: droppable 함수가 허용 type만 수락(문자열 셀렉터 아님)', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const defaults = toModelDefaults(col, [])
    expect(typeof defaults.droppable).toBe('function')
    const drop = defaults.droppable as (s: { is(t: string): boolean }) => boolean
    expect(drop({ is: t => t === 'nx-grid' })).toBe(true)
    expect(drop({ is: t => t === 'nx-section' })).toBe(false)
    const grid = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-grid')!
    expect(toModelDefaults(grid, []).droppable).toBe(false)
  })

  it('toModelDefaults: draggable 함수가 허용 부모만 수락; section은 최상위(true)', () => {
    const row = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-row')!
    const drag = toModelDefaults(row, []).draggable as (s: unknown, t: { is(x: string): boolean }) => boolean
    expect(drag(null, { is: t => t === 'nx-section' })).toBe(true)
    expect(drag(null, { is: t => t === 'nx-column' })).toBe(false)
    const section = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-section')!
    expect(toModelDefaults(section, []).draggable).toBe(true)
  })

  it('toModelDefaults는 트레이트를 그대로 전달', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const traits = buildTraitDefs({ reads: [], writes: [] })['nx-column']
    expect(toModelDefaults(col, traits).traits).toBe(traits)
  })

  it('buildTraitDefs는 grid에 read 쿼리 드롭다운, button에 write 쿼리 드롭다운', () => {
    const traits = buildTraitDefs({ reads: ['MDM.PlantList'], writes: [{ id: 'MDM.CreatePlant', requiredPermission: 'mdm:manage' }] })
    const gridQuery = traits['nx-grid'].find(t => t.name === 'data-query-id')!
    expect(gridQuery.options!.map(o => o.id)).toContain('MDM.PlantList')
    const btnCmd = traits['nx-button'].find(t => t.name === 'data-command')!
    expect(btnCmd.options!.map(o => o.id)).toContain('MDM.CreatePlant')
  })
})
