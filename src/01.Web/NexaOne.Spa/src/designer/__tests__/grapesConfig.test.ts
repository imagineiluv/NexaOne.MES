import { describe, it, expect } from 'vitest'
import { BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildEditorConfig, buildTraitDefs } from '../grapesConfig'

describe('GrapesJS 디자이너 설정(잠금)', () => {
  it('8개 블록만 노출(§5 컴포넌트 세트)', () => {
    expect(BLOCK_DEFS.map(b => b.id).sort()).toEqual(
      ['nx-button', 'nx-column', 'nx-field', 'nx-form', 'nx-grid', 'nx-row', 'nx-section', 'nx-text'].sort())
  })

  it('8개 컴포넌트 type 정의', () => {
    expect(COMPONENT_TYPE_DEFS.map(c => c.type).sort()).toEqual(
      ['nx-button', 'nx-column', 'nx-field', 'nx-form', 'nx-grid', 'nx-row', 'nx-section', 'nx-text'].sort())
  })

  it('init 설정은 RTE·스타일·기본 블록을 잠근다', () => {
    const cfg = buildEditorConfig(document.createElement('div'))
    expect(cfg.storageManager).toBe(false)
    expect(cfg.rte).toBe(false)
    expect(cfg.blockManager?.blocks).toEqual([])
    expect(cfg.styleManager?.sectors).toEqual([])
  })

  it('중첩 규칙: column은 위젯을 droppable, section은 row만', () => {
    const col = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-column')!
    const section = COMPONENT_TYPE_DEFS.find(c => c.type === 'nx-section')!
    expect(col.model.droppable).toContain('nx-grid')
    expect(section.model.droppable).toBe('nx-row')
  })

  it('buildTraitDefs는 grid에 read 쿼리 드롭다운, button에 write 쿼리 드롭다운', () => {
    const traits = buildTraitDefs({ reads: ['MDM.PlantList'], writes: [{ id: 'MDM.CreatePlant', requiredPermission: 'mdm:manage' }] })
    const gridQuery = traits['nx-grid'].find(t => t.name === 'data-query-id')!
    expect(gridQuery.options!.map(o => o.id)).toContain('MDM.PlantList')
    const btnCmd = traits['nx-button'].find(t => t.name === 'data-command')!
    expect(btnCmd.options!.map(o => o.id)).toContain('MDM.CreatePlant')
  })
})
