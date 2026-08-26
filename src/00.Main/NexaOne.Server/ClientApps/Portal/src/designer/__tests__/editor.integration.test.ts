// @vitest-environment jsdom
//
// 실(real) GrapesJS 에디터 통합 테스트 — ScreenEditor.tsx의 init/addType/BlockManager.add/setComponents
// 배선을 그대로 재현해 실제 컴포넌트 모델 위에서 검증한다. 기존 grapesConfig.test/mapping.test는
// 빌더·순수 변환기만(에디터 미마운트) 검증하므로, grapesjs ^0.21.13 API/버전 회귀(addType/
// BlockManager.add/setComponents 사용 깨짐)는 잡지 못한다. 이 테스트가 그 공백을 닫는다.
import { describe, it, expect, afterEach } from 'vitest'
import grapesjs, { type Component, type Editor } from 'grapesjs'
import 'grapesjs/dist/css/grapes.min.css'
import {
  buildEditorConfig, BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildTraitDefs, toModelDefaults,
  syncRequiredPermission,
  type QueryCatalog,
} from '../grapesConfig'
import { layoutToComponent } from '../mapping'
import { readRootLayout } from '../editorBridge'
import type { LayoutNode } from '../layout'

const CATALOG: QueryCatalog = {
  reads: [{ id: 'MDM.PlantList', isWrite: false, requiredPermission: 'mdm:read' }],
  writes: [{ id: 'SYS.Save', isWrite: true, requiredPermission: 'sys:manage' }],
}

// 마운트된 에디터/DOM 추적 — afterEach에서 확정 정리(destroy + 컨테이너 제거).
let editor: Editor | null = null
let containers: HTMLElement[] = []

// ScreenEditor.tsx init 흐름 재현: init → 선언된 type addType → block BlockManager.add.
function mountEditor(): Editor {
  const host = document.createElement('div')
  const blocks = document.createElement('div')
  const traits = document.createElement('div')
  for (const el of [host, blocks, traits]) document.body.appendChild(el)
  containers = [host, blocks, traits]

  const ed = grapesjs.init(buildEditorConfig(host, blocks, traits) as never)
  const traitDefs = buildTraitDefs(CATALOG)
  for (const c of COMPONENT_TYPE_DEFS) {
    ed.DomComponents.addType(c.type, { model: { defaults: toModelDefaults(c, traitDefs[c.type] ?? []) } } as never)
  }
  for (const b of BLOCK_DEFS) {
    ed.BlockManager.add(b.id, { label: b.label, content: b.content as never })
  }
  const syncPermission = (component: Component) => syncRequiredPermission(component, CATALOG)
  ed.on('component:add', syncPermission)
  ed.on('component:update:attributes', syncPermission)
  editor = ed
  return ed
}

afterEach(() => {
  if (editor) { editor.destroy(); editor = null }
  for (const el of containers) el.remove()
  containers = []
})

describe('GrapesJS 실 에디터 통합(마운트)', () => {
  it('1. grapesjs.init은 throw하지 않고 잠금 설정(storageManager=false)이 반영된다', () => {
    const ed = mountEditor()
    expect(ed).toBeDefined()
    // 잠금: 저장 매니저 비활성(저장은 수동 readback 경로).
    expect(ed.getConfig().storageManager).toBe(false)
  })

  it('2. BLOCK_DEFS 블록 id가 실 BlockManager에 모두 등록된다', () => {
    const ed = mountEditor()
    const ids = ed.BlockManager.getAll().map((b: { id: string }) => b.id).sort()
    expect(ids).toEqual(BLOCK_DEFS.map(b => b.id).sort())
  })

  it('3. COMPONENT_TYPE_DEFS type이 실 DomComponents에 resolve된다', () => {
    const ed = mountEditor()
    for (const c of COMPONENT_TYPE_DEFS) {
      expect(ed.DomComponents.getType(c.type)).toBeTruthy()
    }
  })

  it('4. 실 에디터 모델을 통한 라운드트립(setComponents → getComponents → componentToLayout)', () => {
    const ed = mountEditor()
    // 대표 레이아웃: section→row→[grid 컬럼, form(field) + command button]의 2열.
    // 변환기의 옵셔널 정규화(required/readOnly→false, options→null, column.visible→true)에 맞춰
    // 입력을 이미 정규화된 명시값으로 구성 → 라운드트립이 의미상 deep-equal 되도록 한다.
    const layout: LayoutNode = {
      kind: 'section',
      title: '주문 화면',
      children: [
        {
          kind: 'row',
          children: [
            {
              kind: 'column',
              span: 7,
              children: [
                {
                  kind: 'grid',
                  queryId: 'MDM.PlantList',
                  requiredPermission: 'mdm:read',
                  columns: [
                    { key: 'code', caption: '코드', visible: true },
                    { key: 'name', caption: '이름', visible: true },
                  ],
                },
              ],
            },
            {
              kind: 'column',
              span: 5,
              children: [
                {
                  kind: 'form',
                  saveQueryId: 'SYS.Save',
                  requiredPermission: 'sys:manage',
                  fields: [
                    {
                      kind: 'field',
                      fieldKey: 'qty',
                      field: {
                        key: 'qty',
                        label: '수량',
                        type: 'Number',
                        required: false,
                        readOnly: false,
                        options: null,
                      },
                    },
                  ],
                },
                { kind: 'commandButton', label: '저장', command: 'SYS.Save', requiredPermission: 'sys:manage' },
              ],
            },
          ],
        },
      ],
    }

    ed.setComponents([layoutToComponent(layout)] as never)

    // 프로덕션 읽기경로(editorBridge.readRootLayout)를 그대로 통과시켜 회귀를 잡는다 — 디자이너 저장 경로와 동일 코드.
    // 이 함수가 grapesjs 0.21 Component.toJSON()의 `components`(Backbone Collection, 평면 배열 아님)를 JSON
    // 정규화로 환원하지 않으면 모든 자식이 누락돼 빈 루트 섹션만 남는다(데이터 손실 회귀 가드).
    const readBack = readRootLayout(ed)

    expect(readBack).toEqual(layout)
  })

  it('5. droppable/draggable 중첩 규칙이 실 모델에 배선되어 행동으로 강제된다', () => {
    const ed = mountEditor()
    const wrapper = ed.getWrapper()!

    // 실 인스턴스 생성(addType defaults 적용됨).
    const [section] = wrapper.append({ type: 'nx-section' }) as ReturnType<typeof wrapper.append>
    const [grid] = wrapper.append({ type: 'nx-grid' }) as ReturnType<typeof wrapper.append>
    const [row] = wrapper.append({ type: 'nx-row' }) as ReturnType<typeof wrapper.append>

    // droppable은 type 기반 함수(문자열 셀렉터 아님)로 배선 — section은 row 수용, grid(leaf)는 거부.
    const sectionDrop = section.get('droppable') as (src: { is(t: string): boolean }) => boolean
    expect(typeof sectionDrop).toBe('function')
    expect(sectionDrop(row)).toBe(true)   // section은 row를 자식 허용
    expect(sectionDrop(grid)).toBe(false) // section은 grid를 자식 불허(column 경유만)

    // grid는 leaf → droppable false(함수 아닌 boolean), 어떤 자식도 수용 불가.
    expect(grid.get('droppable')).toBe(false)

    // draggable도 함수: row는 부모가 section일 때만 true.
    const rowDrag = row.get('draggable') as (s: unknown, trg: { is(t: string): boolean }) => boolean
    expect(typeof rowDrag).toBe('function')
    expect(rowDrag(row, section)).toBe(true)  // row→section 허용
    expect(rowDrag(row, grid)).toBe(false)    // row→grid 불허
  })

  it('6. 실 component의 binding 변경 이벤트가 권한을 갱신하고 stale 권한을 제거한다', () => {
    const ed = mountEditor()
    const wrapper = ed.getWrapper()!
    const [grid] = wrapper.append({
      type: 'nx-grid',
      attributes: { 'data-query-id': 'MDM.PlantList', 'data-required-permission': 'old:read' },
    }) as ReturnType<typeof wrapper.append>

    expect(grid.getAttributes()['data-required-permission']).toBe('mdm:read')
    grid.addAttributes({ 'data-query-id': 'REMOVED.Query' })
    expect(grid.getAttributes()).not.toHaveProperty('data-required-permission')
  })

  it('7. collection은 실 에디터에서 field 자식을 수용하고 저장 읽기경로에서 보존된다', () => {
    const ed = mountEditor()
    const layout: LayoutNode = {
      kind: 'collection', id: 'qms-items', collectionKey: 'items', label: '검사 항목', itemLabel: '항목', minItems: 1,
      fields: [{
        kind: 'field', id: 'qms-item-spec', fieldKey: 'specId',
        field: { key: 'specId', label: '검사 규격', type: 'Select', required: true, readOnly: false, options: null },
      }],
    }
    ed.setComponents([layoutToComponent(layout)] as never)

    const collection = ed.getWrapper()!.components().at(0)!
    const field = collection.components().at(0)!
    const drop = collection.get('droppable') as (source: { is(type: string): boolean }) => boolean
    const drag = field.get('draggable') as (source: unknown, target: { is(type: string): boolean }) => boolean
    expect(drop(field)).toBe(true)
    expect(drag(field, collection)).toBe(true)
    expect(readRootLayout(ed)).toEqual(layout)
  })
})
