import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import grapesjs, {
  type Editor, type AddComponentTypeOptions, type ComponentDefinition, type ComponentAdd,
} from 'grapesjs'
import 'grapesjs/dist/css/grapes.min.css'
import { getAccessToken } from '../api/client'
import { hasPermission } from '../auth/jwt'
import { loadDefinition, saveDefinition, listQueries } from '../designer/api'
import { layoutToComponent, componentToLayout } from '../designer/mapping'
import {
  buildEditorConfig, BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildTraitDefs, toModelDefaults, type QueryCatalog,
} from '../designer/grapesConfig'
import type { GrapesNode, LayoutNode } from '../designer/layout'

// 캔버스 루트(보통 단일 nx-section)를 스키마 LayoutNode로 환원. 첫 유효 노드만 채택(잠금 규칙상 루트=섹션 1개).
function readRootLayout(editor: Editor): LayoutNode | null {
  // Components(Backbone Collection)의 .models로 Component[]를 명시적으로 받아 toJSON→GrapesNode 경계 캐스팅.
  for (const c of editor.getComponents().models) {
    const node = componentToLayout(c.toJSON() as GrapesNode)
    if (node) return node
  }
  return null
}

export function ScreenEditor() {
  const { uiId } = useParams<{ uiId: string }>()
  const hostRef = useRef<HTMLDivElement>(null)
  const blocksRef = useRef<HTMLDivElement>(null)
  const traitsRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<Editor | null>(null)
  const [title, setTitle] = useState('')
  const [status, setStatus] = useState('초기화 중…')

  const canManage = hasPermission(getAccessToken(), 'sys:manage')

  useEffect(() => {
    if (!hostRef.current || !blocksRef.current || !traitsRef.current || !canManage) return
    let disposed = false
    // grapesjs EditorConfig는 우리 잠금 설정 형태와 정확히 호환되지 않아 init 경계에서만 캐스팅(설정은 grapesConfig 단위테스트가 보증).
    // 블록/트레이트 매니저는 전용 컨테이너(appendTo)에 마운트 — 기본 패널 비활성(panels.defaults=[]) 상태에서도 노출된다.
    const editor = grapesjs.init(buildEditorConfig(hostRef.current, blocksRef.current, traitsRef.current) as never)
    editorRef.current = editor

    listQueries()
      .then((cat: QueryCatalog) => {
        if (disposed) return { title: '', layout: null as LayoutNode | null }
        const traits = buildTraitDefs(cat)
        for (const c of COMPONENT_TYPE_DEFS) {
          // 중첩 규칙은 문자열(CSS 셀렉터)이 아니라 type 기반 함수(toModelDefaults)로 줘야 드롭이 동작한다.
          editor.DomComponents.addType(c.type, {
            model: { defaults: toModelDefaults(c, traits[c.type] ?? []) },
          } as AddComponentTypeOptions)
        }
        for (const b of BLOCK_DEFS) {
          editor.BlockManager.add(b.id, { label: b.label, content: b.content as ComponentDefinition })
        }
        return uiId ? loadDefinition(uiId) : { title: '', layout: null as LayoutNode | null }
      })
      .then(({ title: loaded, layout }) => {
        if (disposed) return
        setTitle(loaded || (uiId ?? ''))
        const root: GrapesNode = layout
          ? layoutToComponent(layout)
          : { type: 'nx-section', attributes: {}, components: [] }
        editor.setComponents([root] as ComponentAdd)
        setStatus('준비됨')
      })
      .catch(() => { if (!disposed) setStatus('로드 실패(권한/네트워크 확인)') })

    return () => { disposed = true; editor.destroy(); editorRef.current = null }
  }, [uiId, canManage])

  async function handleSave() {
    const editor = editorRef.current
    if (!editor || !uiId) return
    try {
      setStatus('저장 중…')
      await saveDefinition(uiId, title || uiId, readRootLayout(editor))
      setStatus('저장됨')
    } catch {
      setStatus('저장 실패(권한 sys:manage 확인)')
    }
  }

  if (!canManage) return <div style={{ padding: '2rem' }}>화면 디자이너 권한(sys:manage)이 없습니다.</div>

  return (
    <div style={{ display: 'flex', flexDirection: 'column', height: '100vh' }}>
      <header style={{ display: 'flex', gap: 8, alignItems: 'center', padding: 8, borderBottom: '1px solid #ddd' }}>
        <strong>화면 디자이너</strong>
        <input aria-label="화면 제목" value={title} onChange={e => setTitle(e.target.value)} placeholder="화면 제목" />
        <span>UI ID: {uiId ?? '(미지정)'}</span>
        <button onClick={handleSave} disabled={!uiId}>저장</button>
        <span style={{ marginLeft: 'auto' }}>{status}</span>
      </header>
      <div style={{ display: 'flex', flex: 1, minHeight: 0 }}>
        <div ref={blocksRef} style={{ width: 200, overflow: 'auto', borderRight: '1px solid #ddd' }} />
        <div ref={hostRef} style={{ flex: 1, minHeight: 0 }} />
        <div ref={traitsRef} style={{ width: 240, overflow: 'auto', borderLeft: '1px solid #ddd' }} />
      </div>
    </div>
  )
}
