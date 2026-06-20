import { describe, it, expect } from 'vitest'
import type { LayoutNode } from '../layout'
import { layoutToComponent, componentToLayout, buildDefinitionJson, parseDefinition } from '../mapping'

const golden: LayoutNode = {
  kind: 'section', id: 'sec-root', title: '공장 마스터',
  children: [{
    kind: 'row', id: 'row-1', children: [
      { kind: 'column', id: 'col-1', span: 7, children: [
        { kind: 'grid', id: 'grid-plants', queryId: 'MDM.PlantList',
          columns: [{ key: 'PLANT_ID', caption: '공장 ID', visible: true }] },
      ] },
      { kind: 'column', id: 'col-2', span: 5, children: [
        { kind: 'form', id: 'form-plant', saveQueryId: 'MDM.CreatePlant', fields: [
          { kind: 'field', id: 'fld-1', fieldKey: 'plantId',
            field: { key: 'plantId', label: '공장 ID', type: 'Text', required: true, readOnly: false, options: null } },
        ] },
        { kind: 'commandButton', id: 'btn-1', label: '저장', command: 'MDM.CreatePlant', requiredPermission: 'mdm:manage' },
      ] },
    ],
  }],
}

describe('LayoutNode ↔ GrapesJS 매핑', () => {
  it('레이아웃→컴포넌트→레이아웃 라운드트립 무손실', () => {
    const comp = layoutToComponent(golden)
    const back = componentToLayout(comp)
    expect(back).toEqual(golden)
  })

  it('각 노드의 GrapesJS type이 kind에 대응', () => {
    expect(layoutToComponent(golden).type).toBe('nx-section')
    const grid = layoutToComponent({ kind: 'grid', id: 'g', queryId: 'Q' })
    expect(grid.type).toBe('nx-grid')
    expect(grid.attributes!['data-query-id']).toBe('Q')
  })

  it('미지 type은 null로 격리(전체 트리 깨뜨리지 않음)', () => {
    const back = componentToLayout({ type: 'textnode', components: [] })
    expect(back).toBeNull()
  })
})

describe('정의 직렬화', () => {
  it('buildDefinitionJson은 §5 형식(camelCase·평면 필드 빈 배열·layout 포함)을 만든다', () => {
    const json = buildDefinitionJson('PLANT_MGMT', '공장 관리', golden)
    const parsed = JSON.parse(json)
    expect(parsed.uiId).toBe('PLANT_MGMT')
    expect(parsed.title).toBe('공장 관리')
    expect(parsed.fields).toEqual([])
    expect(parsed.layout.kind).toBe('section')
    expect(parsed.layout.children[0].children[0].children[0].queryId).toBe('MDM.PlantList')
  })

  it('parseDefinition은 DEFINITION_JSON에서 title·layout을 복원', () => {
    const json = buildDefinitionJson('X', '타이틀', golden)
    const { title, layout } = parseDefinition(json)
    expect(title).toBe('타이틀')
    expect(layout).toEqual(golden)
  })

  it('parseDefinition은 깨진 JSON에 null layout 반환(throw 금지)', () => {
    expect(parseDefinition('not json')).toEqual({ title: '', layout: null })
  })

  it('parseDefinition은 layout 없는 레거시 평면 정의에 layout=null', () => {
    const json = JSON.stringify({ uiId: 'L', title: '레거시', fields: [], columns: null })
    expect(parseDefinition(json)).toEqual({ title: '레거시', layout: null })
  })
})
