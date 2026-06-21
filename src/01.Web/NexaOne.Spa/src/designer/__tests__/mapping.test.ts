import { describe, it, expect } from 'vitest'
import type { LayoutNode, ScreenDefinitionDto } from '../layout'
import { layoutToComponent, componentToLayout, buildDefinitionJson, parseDefinition, flatToLayout } from '../mapping'

// 부분 속성으로 평면 dto를 만드는 테스트 헬퍼(미지정은 빈 기본값).
function dto(p: Partial<ScreenDefinitionDto>): ScreenDefinitionDto {
  return {
    uiId: '', title: '', fields: [], columns: null, queryId: null, saveQueryId: null, layout: null,
    ...p,
  }
}

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

describe('field 이산 속성 매핑(트레이트 단일 출처)', () => {
  it('field→컴포넌트가 이산 data-field-* 속성을 기록(data-field JSON blob 미기록)', () => {
    const comp = layoutToComponent({
      kind: 'field', id: 'f', fieldKey: 'plantId',
      field: { key: 'plantId', label: '공장 ID', type: 'Text', required: true, readOnly: false, options: null },
    })
    const a = comp.attributes!
    expect(a['data-field']).toBeUndefined()
    expect(a['data-field-key']).toBe('plantId')
    expect(a['data-field-label']).toBe('공장 ID')
    expect(a['data-field-type']).toBe('Text')
    expect(a['data-field-required']).toBe(true)
    // readOnly=false·options=null은 미기록(조건부 속성)
    expect(a['data-field-readonly']).toBeUndefined()
    expect(a['data-field-options']).toBeUndefined()
  })

  it('5속성 field 라운드트립 무손실(layoutToComponent→componentToLayout 동일성)', () => {
    const node: LayoutNode = {
      kind: 'field', id: 'f1', fieldKey: 'qty',
      field: { key: 'qty', label: '수량', type: 'Number', required: true, readOnly: true, options: null },
    }
    expect(componentToLayout(layoutToComponent(node))).toEqual(node)
  })

  it('Select 필드 options 라운드트립', () => {
    const node: LayoutNode = {
      kind: 'field', id: 'f2', fieldKey: 'status',
      field: { key: 'status', label: '상태', type: 'Select', required: false, readOnly: false, options: ['A', 'B', 'C'] },
    }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-field-options']).toBe('A,B,C')
    expect(comp.attributes!['data-field-type']).toBe('Select')
    expect(componentToLayout(comp)).toEqual(node)
  })

  it('키만 있는 베어 필드는 field 없이 fieldKey만 유지(하위호환)', () => {
    const node: LayoutNode = { kind: 'field', id: 'f3', fieldKey: 'onlyKey' }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-field-key']).toBe('onlyKey')
    expect(comp.attributes!['data-field-label']).toBeUndefined()
    const back = componentToLayout(comp)
    expect(back).toEqual(node)
    expect((back as Extract<LayoutNode, { kind: 'field' }>).field).toBeUndefined()
  })
})

describe('grid 컬럼 spec 매핑(트레이트 단일 출처)', () => {
  it('3컬럼(숨김 1개 포함) 라운드트립(data-columns JSON blob 미기록)', () => {
    const node: LayoutNode = {
      kind: 'grid', id: 'g1', queryId: 'MDM.PlantList',
      columns: [
        { key: 'code', caption: '코드', visible: true },
        { key: 'name', caption: '이름', visible: true },
        { key: 'secret', caption: '비밀', visible: false },
      ],
    }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-columns']).toBeUndefined()
    expect(comp.attributes!['data-columns-spec']).toBe('code:코드, name:이름, secret:비밀:hidden')
    expect(comp.attributes!['data-query-id']).toBe('MDM.PlantList')
    expect(componentToLayout(comp)).toEqual(node)
  })

  it('컬럼 없는 그리드는 spec 미기록, columns 미복원', () => {
    const node: LayoutNode = { kind: 'grid', id: 'g2', queryId: 'Q' }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-columns-spec']).toBeUndefined()
    const back = componentToLayout(comp) as Extract<LayoutNode, { kind: 'grid' }>
    expect(back.columns).toBeUndefined()
    expect(back.queryId).toBe('Q')
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
    expect(parseDefinition('not json')).toEqual({ title: '', layout: null, flat: null })
  })

  it('parseDefinition은 layout 없는 레거시 평면 정의에 layout=null', () => {
    const json = JSON.stringify({ uiId: 'L', title: '레거시', fields: [], columns: null })
    expect(parseDefinition(json)).toMatchObject({ title: '레거시', layout: null })
  })
})

describe('flatToLayout — 레거시 평면 정의→레이아웃 합성(단방향)', () => {
  type Section = Extract<LayoutNode, { kind: 'section' }>
  type Row = Extract<LayoutNode, { kind: 'row' }>
  type Column = Extract<LayoutNode, { kind: 'column' }>

  it('그리드만(columns만, fields 없음)이면 단일열 span12', () => {
    const out = flatToLayout(dto({
      uiId: 'G', title: '그리드화면', queryId: 'MDM.PlantList',
      columns: [{ key: 'PLANT_ID', caption: '공장 ID', visible: true }],
    })) as Section
    expect(out.kind).toBe('section')
    expect(out.title).toBe('그리드화면')
    const row = out.children![0] as Row
    expect(row.kind).toBe('row')
    expect(row.children).toHaveLength(1)
    const col = row.children![0] as Column
    expect(col.kind).toBe('column')
    expect(col.span).toBe(12)
    expect(col.children![0]).toMatchObject({ kind: 'grid', queryId: 'MDM.PlantList' })
  })

  it('폼만(fields만)이면 단일열 span12 + saveQueryId면 저장버튼', () => {
    const out = flatToLayout(dto({
      uiId: 'F', title: '폼화면', saveQueryId: 'MDM.CreatePlant',
      fields: [{ key: 'plantId', label: '공장 ID', type: 'Text', required: true, readOnly: false, options: null }],
    })) as Section
    const row = out.children![0] as Row
    expect(row.children).toHaveLength(1)
    const col = row.children![0] as Column
    expect(col.span).toBe(12)
    expect(col.children![0]).toMatchObject({ kind: 'form', saveQueryId: 'MDM.CreatePlant' })
    expect(col.children![1]).toMatchObject({ kind: 'commandButton', label: '저장', command: 'MDM.CreatePlant' })
  })

  it('폼만 + saveQueryId 없으면 저장버튼 미생성', () => {
    const out = flatToLayout(dto({
      uiId: 'F2', title: '폼만', saveQueryId: null,
      fields: [{ key: 'k', label: 'L', type: 'Text', required: false, readOnly: false, options: null }],
    })) as Section
    const col = (out.children![0] as Row).children![0] as Column
    expect(col.children).toHaveLength(1)
    expect(col.children![0]).toMatchObject({ kind: 'form' })
  })

  it('그리드+폼 둘 다면 2열(7/5)', () => {
    const out = flatToLayout(dto({
      uiId: 'B', title: '둘다', queryId: 'MDM.PlantList', saveQueryId: 'MDM.CreatePlant',
      columns: [{ key: 'PLANT_ID', caption: '공장 ID', visible: true }],
      fields: [{ key: 'plantId', label: '공장 ID', type: 'Text', required: true, readOnly: false, options: null }],
    })) as Section
    const row = out.children![0] as Row
    expect(row.children).toHaveLength(2)
    const gridCol = row.children![0] as Column
    const formCol = row.children![1] as Column
    expect(gridCol.span).toBe(7)
    expect(gridCol.children![0]).toMatchObject({ kind: 'grid' })
    expect(formCol.span).toBe(5)
    expect(formCol.children![0]).toMatchObject({ kind: 'form' })
    expect(formCol.children![1]).toMatchObject({ kind: 'commandButton', command: 'MDM.CreatePlant' })
  })

  it('그리드·폼 둘 다 없으면 빈 섹션(children=[])', () => {
    const out = flatToLayout(dto({ uiId: 'E', title: '빈화면' })) as Section
    expect(out).toMatchObject({ kind: 'section', title: '빈화면', children: [] })
  })

  it('columns 비고 queryId만이면 그리드 미생성(빈 섹션)', () => {
    const out = flatToLayout(dto({ uiId: 'Q', title: 'queryId만', queryId: 'MDM.PlantList', columns: [] })) as Section
    expect(out.children).toEqual([])
  })

  it('5속성 field 라운드트립 무손실(componentToLayout(layoutToComponent(out)) === out)', () => {
    const out = flatToLayout(dto({
      uiId: 'RT', title: '라운드트립', queryId: 'MDM.PlantList', saveQueryId: 'MDM.CreatePlant',
      columns: [{ key: 'PLANT_ID', caption: '공장 ID', visible: true }],
      fields: [{ key: 'plantId', label: '공장 ID', type: 'Text', required: true, readOnly: false, options: null }],
    }))
    const back = componentToLayout(layoutToComponent(out))
    expect(back).toEqual(out)
  })

  it('모든 노드에 결정론적 id가 부여된다', () => {
    const out = flatToLayout(dto({
      uiId: 'IDS', queryId: 'Q', saveQueryId: 'S',
      columns: [{ key: 'C', caption: 'c', visible: true }],
      fields: [{ key: 'fk', label: 'L', type: 'Text', required: false, readOnly: false, options: null }],
    })) as Section
    expect(out.id).toBe('sec-IDS')
    const row = out.children![0] as Row
    expect(row.id).toBe('row-IDS')
    const gridCol = row.children![0] as Column
    const formCol = row.children![1] as Column
    expect(gridCol.id).toBe('col-grid-IDS')
    expect(gridCol.children![0].id).toBe('grid-IDS')
    expect(formCol.id).toBe('col-form-IDS')
    expect(formCol.children![0].id).toBe('form-IDS')
    expect(formCol.children![0].kind === 'form' && formCol.children![0].fields![0].id).toBe('fld-fk')
    expect(formCol.children![1].id).toBe('btn-save-IDS')
  })
})
