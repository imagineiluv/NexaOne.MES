import { useEffect, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import grapesjs, {
  type Editor, type AddComponentTypeOptions, type ComponentDefinition, type ComponentAdd,
} from 'grapesjs'
import 'grapesjs/dist/css/grapes.min.css'
import { getAccessToken } from '../api/client'
import { hasPermission } from '../auth/jwt'
import { loadDefinition, saveDefinition, listQueries } from '../designer/api'
import { layoutToComponent, flatToLayout } from '../designer/mapping'
import { readRootLayout } from '../designer/editorBridge'
import {
  buildEditorConfig, BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildTraitDefs, toModelDefaults, type QueryCatalog,
} from '../designer/grapesConfig'
import type { GrapesNode, LayoutNode, ScreenDefinitionDto } from '../designer/layout'

export function ScreenEditor() {
  const { uiId } = useParams<{ uiId: string }>()
  const hostRef = useRef<HTMLDivElement>(null)
  const blocksRef = useRef<HTMLDivElement>(null)
  const traitsRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<Editor | null>(null)
  // 화면 수준 설정(자동 새로고침·검색 조건) 보존용 — 디자이너는 레이아웃만 편집하므로 로드 시
  // flat을 붙들었다가 저장에 그대로 실어야 재저장이 이 설정들을 드랍하지 않는다.
  const flatRef = useRef<ScreenDefinitionDto | null>(null)
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

    // P3-17 다크 모드 — GrapesJS 캔버스는 iframe이라 부모의 data-theme를 상속하지 않는다.
    // 로드 시 캔버스 문서에 테마 속성 + 배경/텍스트 토큰 스타일을 주입한다(다크에서 흰 캔버스 방지).
    editor.on('load', () => {
      try {
        const theme = document.documentElement.dataset.theme || 'light'
        const cdoc = editor.Canvas.getDocument()
        if (!cdoc) return
        cdoc.documentElement.dataset.theme = theme
        const style = cdoc.createElement('style')
        style.textContent =
          '[data-theme="dark"]{background:#0d1420;color:#dfe6f1;}' +
          '[data-theme="dark"] *{border-color:#2b3750 !important;}'
        cdoc.head.appendChild(style)
      } catch { /* 캔버스 접근 실패(테스트 jsdom 등) — 무시 */ }
    })

    listQueries()
      .then((cat: QueryCatalog) => {
        if (disposed) return { title: '', layout: null as LayoutNode | null, flat: null }
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
        return uiId ? loadDefinition(uiId) : { title: '', layout: null as LayoutNode | null, flat: null }
      })
      .then(({ title: loaded, layout, flat }) => {
        if (disposed) return
        flatRef.current = flat
        setTitle(loaded || (uiId ?? ''))
        const effective: LayoutNode | null = layout ?? (flat ? flatToLayout(flat) : null)
        const root: GrapesNode = effective
          ? layoutToComponent(effective)
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
      await saveDefinition(uiId, title || uiId, readRootLayout(editor), {
        refreshIntervalSeconds: flatRef.current?.refreshIntervalSeconds,
        searchFields: flatRef.current?.searchFields,
        countQueryId: flatRef.current?.countQueryId,
        deleteQueryId: flatRef.current?.deleteQueryId,
        bulkCommands: flatRef.current?.bulkCommands,
      })
      setStatus('저장됨')
    } catch {
      setStatus('저장 실패(권한 sys:manage 확인)')
    }
  }

  if (!canManage) return <div className="nx-gate">화면 디자이너 권한(sys:manage)이 없습니다.</div>

  return (
    <div className="nx-designer">
      <header className="nx-designer-bar">
        <span className="nx-brand">화면 디자이너<small>SCREEN DESIGNER</small></span>
        <input className="nx-input" aria-label="화면 제목" value={title} onChange={e => setTitle(e.target.value)} placeholder="화면 제목" />
        <span className="nx-uiid">UI ID: {uiId ?? '(미지정)'}</span>
        <button className="nx-btn nx-btn-teal" onClick={handleSave} disabled={!uiId}>저장</button>
        <span className="nx-status">{status}</span>
      </header>
      <div className="nx-designer-body">
        <div className="nx-panel nx-panel-blocks">
          <div className="nx-panel-head">블록</div>
          <div ref={blocksRef} className="nx-panel-scroll" />
        </div>
        <div ref={hostRef} className="nx-canvas" />
        <div className="nx-panel nx-panel-traits">
          <div className="nx-panel-head">속성</div>
          <div ref={traitsRef} className="nx-panel-scroll" />
        </div>
      </div>
    </div>
  )
}
