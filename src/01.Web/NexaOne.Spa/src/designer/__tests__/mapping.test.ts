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

  it('KPI 카드(Phase-2) 라운드트립 무손실 — 런타임 KPI 화면을 디자이너가 열어도 드랍되지 않는다', () => {
    const kpi: LayoutNode = {
      kind: 'kpi', id: 'kpi-1', label: '활성 알람',
      queryId: 'SYS.DashboardSummary', valueColumn: 'ACTIVE_ALARMS', unit: '건',
    }
    const comp = layoutToComponent(kpi)
    expect(comp.type).toBe('nx-kpi')
    expect(comp.attributes!['data-value-column']).toBe('ACTIVE_ALARMS')
    expect(componentToLayout(comp)).toEqual(kpi)

    // 선택 속성(queryId/valueColumn/unit) 없는 베어 KPI도 라운드트립 안전
    const bare: LayoutNode = { kind: 'kpi', id: 'kpi-2', label: 'KPI' }
    expect(componentToLayout(layoutToComponent(bare))).toEqual(bare)
  })

  it('StatusBadge(Phase-2) 라운드트립 무손실 — 스타일 규칙(JSON 인코딩)에 구분자가 있어도 보존', () => {
    const badge: LayoutNode = {
      kind: 'statusBadge', id: 'bdg-1', label: '설비 상태',
      queryId: 'EST.CurrentState', valueColumn: 'STATE_ID',
      styles: [
        { value: 'RUN', severity: 'success', displayText: '가동, 정상' },   // 콤마 포함 displayText
        { value: 'DOWN', severity: 'danger' },
      ],
    }
    const comp = layoutToComponent(badge)
    expect(comp.type).toBe('nx-badge-widget')
    expect(typeof comp.attributes!['data-styles']).toBe('string')
    expect(componentToLayout(comp)).toEqual(badge)

    const bare: LayoutNode = { kind: 'statusBadge', id: 'bdg-2' }
    expect(componentToLayout(layoutToComponent(bare))).toEqual(bare)
  })

  it('멀티폼 isolated(Phase-2) 라운드트립 — true만 기록, 미지정은 속성 부재(하위호환)', () => {
    const isolated: LayoutNode = {
      kind: 'form', id: 'form-a', saveQueryId: 'MDM.CreatePlant', isolated: true,
      fields: [{ kind: 'field', id: 'f1', fieldKey: 'plantId' }],
    }
    const comp = layoutToComponent(isolated)
    expect(comp.attributes!['data-isolated']).toBe(true)
    expect(componentToLayout(comp)).toEqual(isolated)

    const shared: LayoutNode = { kind: 'form', id: 'form-b', saveQueryId: 'MDM.CreateArea', fields: [] }
    const sharedComp = layoutToComponent(shared)
    expect(sharedComp.attributes!['data-isolated']).toBeUndefined()
    expect(componentToLayout(sharedComp)).toEqual(shared)
  })

  it('TrendChart(Phase-2 실시간 v2) 라운드트립 무손실 — maxPoints 숫자 속성 포함', () => {
    const chart: LayoutNode = {
      kind: 'trendChart', id: 'tc-1', label: '온도 트렌드',
      queryId: 'FDC.CollectDataList', valueColumn: 'VALUE', maxPoints: 60,
    }
    const comp = layoutToComponent(chart)
    expect(comp.type).toBe('nx-trend-chart')
    expect(componentToLayout(comp)).toEqual(chart)

    const bare: LayoutNode = { kind: 'trendChart', id: 'tc-2', label: '트렌드' }
    expect(componentToLayout(layoutToComponent(bare))).toEqual(bare)
  })

  it('refreshIntervalSeconds(화면 수준)가 build/parse 왕복에서 보존된다 — 재저장 드랍 방지', () => {
    const json = buildDefinitionJson('LIVE1', '실시간', null, 10)
    const parsed = parseDefinition(json)
    expect(parsed.flat!.refreshIntervalSeconds).toBe(10)

    // 미지정이면 필드 자체가 없고(하위호환) 파싱은 null 정규화.
    const legacy = parseDefinition(buildDefinitionJson('OLD1', '기존', null))
    expect(legacy.flat!.refreshIntervalSeconds).toBeNull()
  })

  it('searchFields(검색 조건, 화면 수준)가 build/parse 왕복에서 보존된다 — 재저장 드랍 방지', () => {
    const search = [
      { key: 'logLevel', label: '레벨', type: 'Select' as const, options: ['Warning', 'Error'] },
      { key: 'userId', label: '사용자 ID', type: 'Text' as const },
    ]
    const parsed = parseDefinition(buildDefinitionJson('LOGV', '로그 뷰어', null, null, search))
    expect(parsed.flat!.searchFields).toEqual(search)

    // 미지정/빈 배열이면 필드 자체가 없고(하위호환) 파싱은 null 정규화.
    const none = parseDefinition(buildDefinitionJson('OLD2', '기존', null, null, []))
    expect(none.flat!.searchFields).toBeNull()
  })

  it('countQueryId(서버측 페이징, 화면 수준)가 build/parse 왕복에서 보존된다 — 재저장 드랍 방지', () => {
    const parsed = parseDefinition(buildDefinitionJson('LOGV2', '로그 뷰어', null, null, null, 'SYS.AppLogListCount'))
    expect(parsed.flat!.countQueryId).toBe('SYS.AppLogListCount')

    // 미지정/빈 문자열이면 필드 자체가 없고(하위호환) 파싱은 null 정규화.
    const none = parseDefinition(buildDefinitionJson('OLD3', '기존', null, null, null, ''))
    expect(none.flat!.countQueryId).toBeNull()
  })

  it('그리드 컬럼 width(px, Phase-2)가 JSON 인코딩 라운드트립에서 보존된다', () => {
    const grid: LayoutNode = {
      kind: 'grid', id: 'g-w', queryId: 'MDM.PlantList',
      columns: [
        { key: 'PLANT_ID', caption: '공장 ID', visible: true, width: 120 },
        { key: 'PLANT_NAME', caption: '공장명', visible: true },   // 미지정 → 자동(속성 부재 유지)
      ],
    }
    expect(componentToLayout(layoutToComponent(grid))).toEqual(grid)
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

  it('Select 필드 options 라운드트립(JSON 인코딩)', () => {
    const node: LayoutNode = {
      kind: 'field', id: 'f2', fieldKey: 'status',
      field: { key: 'status', label: '상태', type: 'Select', required: false, readOnly: false, options: ['A', 'B', 'C'] },
    }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-field-options']).toBe(JSON.stringify(['A', 'B', 'C']))
    expect(comp.attributes!['data-field-type']).toBe('Select')
    expect(componentToLayout(comp)).toEqual(node)
  })

  it('동적 옵션 optionsQueryId 라운드트립(data-field-options-query) — 부재 시 속성 미기록', () => {
    const node: LayoutNode = {
      kind: 'field', id: 'f-dyn', fieldKey: 'roleId',
      field: { key: 'roleId', label: '역할', type: 'Select', required: true, readOnly: false, options: null, optionsQueryId: 'SYS.ListRoles' },
    }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-field-options-query']).toBe('SYS.ListRoles')
    expect(componentToLayout(comp)).toEqual(node)

    // 부재 시 속성 자체가 없어야 기존 화면 정의가 재저장에서 불변이다.
    const plain = layoutToComponent({
      kind: 'field', id: 'f-plain', fieldKey: 'name',
      field: { key: 'name', label: '이름', type: 'Text', required: false, readOnly: false, options: null },
    })
    expect(plain.attributes!['data-field-options-query']).toBeUndefined()
  })

  it('KPI linkUiId·트렌드 timeColumn 라운드트립 — 부재 시 속성 미기록', () => {
    const kpi: LayoutNode = { kind: 'kpi', id: 'k-1', label: '활성 알람', queryId: 'Q.A', valueColumn: 'CNT', linkUiId: 'EES_POPUP_MONITERING_DASHBOARD' }
    const kc = layoutToComponent(kpi)
    expect(kc.attributes!['data-link-uiid']).toBe('EES_POPUP_MONITERING_DASHBOARD')
    expect(componentToLayout(kc)).toEqual(kpi)
    expect(layoutToComponent({ kind: 'kpi', id: 'k-2', label: 'KPI' }).attributes!['data-link-uiid']).toBeUndefined()

    const trend: LayoutNode = { kind: 'trendChart', id: 't-1', label: '트렌드', queryId: 'Q.T', valueColumn: 'VALUE', maxPoints: 60, timeColumn: 'COLLECTED_AT' }
    const tc = layoutToComponent(trend)
    expect(tc.attributes!['data-time-column']).toBe('COLLECTED_AT')
    expect(componentToLayout(tc)).toEqual(trend)
  })

  it('버튼 confirmMessage 라운드트립(data-confirm) — 부재 시 속성 미기록', () => {
    const node: LayoutNode = {
      kind: 'commandButton', id: 'b-del', label: '삭제',
      command: 'SYS.DeleteMenuRole', confirmMessage: '정말 삭제하시겠습니까?',
    }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-confirm']).toBe('정말 삭제하시겠습니까?')
    expect(componentToLayout(comp)).toEqual(node)

    const plain = layoutToComponent({ kind: 'commandButton', id: 'b-save', label: '저장', command: 'SYS.Upsert' })
    expect(plain.attributes!['data-confirm']).toBeUndefined()
  })

  it('MAP-1: 콤마·콜론 포함 options가 라운드트립에서 정확히 보존된다(콤마-조인 부패 방지)', () => {
    const node: LayoutNode = {
      kind: 'field', id: 'f-opts', fieldKey: 'cat',
      field: {
        key: 'cat', label: '분류', type: 'Select', required: false, readOnly: false,
        options: ['A, inc.', 'B: type', 'plain'],
      },
    }
    const comp = layoutToComponent(node)
    // 구버전(콤마-조인)이면 'A, inc.'가 두 옵션으로 쪼개졌다 — JSON 인코딩은 단일 문자열로 정확히 보존.
    const back = componentToLayout(comp) as Extract<LayoutNode, { kind: 'field' }>
    expect(back).toEqual(node)
    expect(back.field!.options).toEqual(['A, inc.', 'B: type', 'plain'])
  })

  it('MAP-1: 깨진 data-field-options(비-JSON)는 options=null로 관용 폴백', () => {
    const back = componentToLayout({
      type: 'nx-field',
      attributes: { 'data-field-key': 'k', 'data-field-label': 'L', 'data-field-type': 'Select', 'data-field-options': 'not json' },
    }) as Extract<LayoutNode, { kind: 'field' }>
    expect(back.field!.options).toBeNull()
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

describe('grid 컬럼 매핑(트레이트 단일 출처, JSON 인코딩)', () => {
  it('3컬럼(숨김 1개 포함) 라운드트립(data-columns JSON 기록, 구 spec 미기록)', () => {
    const node: LayoutNode = {
      kind: 'grid', id: 'g1', queryId: 'MDM.PlantList',
      columns: [
        { key: 'code', caption: '코드', visible: true },
        { key: 'name', caption: '이름', visible: true },
        { key: 'secret', caption: '비밀', visible: false },
      ],
    }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-columns-spec']).toBeUndefined()
    expect(comp.attributes!['data-columns']).toBe(JSON.stringify(node.columns))
    expect(comp.attributes!['data-query-id']).toBe('MDM.PlantList')
    expect(componentToLayout(comp)).toEqual(node)
  })

  it('MAP-2: 콤마·콜론 포함 caption + visible:false가 라운드트립에서 정확히 보존된다(spec 부패 방지)', () => {
    const node: LayoutNode = {
      kind: 'grid', id: 'g-delim', queryId: 'Q',
      columns: [
        { key: 'c1', caption: '코드, 번호', visible: true },
        { key: 'c2', caption: 'a:b', visible: false },
      ],
    }
    const comp = layoutToComponent(node)
    // 구버전(key:caption[:hidden] spec)이면 '코드, 번호'의 콤마가 컬럼 경계로, 'a:b'의 콜론이 필드 경계로
    // 오인돼 부패했다 — JSON 인코딩은 두 caption과 visible:false를 정확히 보존한다.
    const back = componentToLayout(comp) as Extract<LayoutNode, { kind: 'grid' }>
    expect(back).toEqual(node)
    expect(back.columns).toEqual([
      { key: 'c1', caption: '코드, 번호', visible: true },
      { key: 'c2', caption: 'a:b', visible: false },
    ])
  })

  it('컬럼 없는 그리드는 data-columns 미기록, columns 미복원', () => {
    const node: LayoutNode = { kind: 'grid', id: 'g2', queryId: 'Q' }
    const comp = layoutToComponent(node)
    expect(comp.attributes!['data-columns']).toBeUndefined()
    const back = componentToLayout(comp) as Extract<LayoutNode, { kind: 'grid' }>
    expect(back.columns).toBeUndefined()
    expect(back.queryId).toBe('Q')
  })

  it('MAP-2: 깨진 data-columns(비-JSON)는 columns 미복원(관용 폴백)', () => {
    const back = componentToLayout({
      type: 'nx-grid', attributes: { 'data-query-id': 'Q', 'data-columns': 'not json' },
    }) as Extract<LayoutNode, { kind: 'grid' }>
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
