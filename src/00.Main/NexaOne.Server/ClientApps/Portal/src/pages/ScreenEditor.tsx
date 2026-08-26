import { useEffect, useRef, useState } from 'react'
import { Link, useNavigate, useParams } from 'react-router-dom'
import grapesjs, {
  type Editor, type AddComponentTypeOptions, type ComponentDefinition, type ComponentAdd, type Component,
} from 'grapesjs'
import 'grapesjs/dist/css/grapes.min.css'
import { ApiError, getAccessToken } from '../api/client'
import { hasPermission } from '../auth/jwt'
import {
  TARGET_CHANNELS,
  entryPathFor,
  importScreenSeed,
  loadDefinition,
  saveDefinition,
  listQueries,
  type LoadedScreenDefinition,
  type ScreenCapabilityDiagnostic,
  type ScreenDefinitionSource,
  type TargetChannel,
} from '../designer/api'
import { layoutToComponent, flatToLayout } from '../designer/mapping'
import { readRootLayout } from '../designer/editorBridge'
import {
  buildEditorConfig, BLOCK_DEFS, COMPONENT_TYPE_DEFS, buildTraitDefs, toModelDefaults, type QueryCatalog,
  syncRequiredPermission, type BlockDef,
} from '../designer/grapesConfig'
import { SCREEN_PURPOSE_VALUES, type GrapesNode, type LayoutNode, type ScreenDefinitionDto, type ScreenPurpose } from '../designer/layout'
import {
  DEFAULT_MANAGE_PREVIEW_MODE,
  MANAGE_PREVIEW_MODES,
  describeManagePreviewMode,
  type ManagePreviewMode,
} from '../designer/managePreview'

const SCREEN_PURPOSE_LABELS: Record<ScreenPurpose, string> = {
  Auto: '자동 판단',
  Register: '등록',
  Manage: '관리',
  Inquiry: '조회',
  Report: '현황·리포트',
  Execute: '작업 실행',
}

const COMPONENT_LABELS: Record<string, string> = {
  'nx-section': '섹션',
  'nx-row': '행',
  'nx-column': '열',
  'nx-grid': '데이터 그리드',
  'nx-form': '폼',
  'nx-collection': '반복 항목',
  'nx-field': '필드',
  'nx-button': '명령 버튼',
  'nx-text': '텍스트',
  'nx-kpi': 'KPI 카드',
  'nx-badge-widget': '상태 뱃지',
  'nx-trend-chart': '트렌드 차트',
}

function normalizeScreenPurpose(value: ScreenPurpose | null | undefined): ScreenPurpose {
  return value && SCREEN_PURPOSE_VALUES.includes(value) ? value : SCREEN_PURPOSE_VALUES[0]
}

function describeComponent(component: Component): string {
  const definition = COMPONENT_TYPE_DEFS.find(item => component.is(item.type))
  if (!definition) return '컴포넌트'
  const attributes = component.getAttributes()
  const detailKeys = ['data-title', 'data-label', 'data-field-label', 'data-field-key', 'data-query-id']
  const detail = detailKeys
    .map(key => attributes[key])
    .find(value => typeof value === 'string' && value.trim().length > 0)
  const label = COMPONENT_LABELS[definition.type] ?? definition.name
  return typeof detail === 'string' ? `${label} · ${detail}` : label
}

function markedBlockContent(block: BlockDef): ComponentDefinition {
  return {
    ...block.content,
    attributes: { ...block.content.attributes, 'data-nx-component': block.id },
  } as ComponentDefinition
}

function findInsertionTarget(editor: Editor, block: BlockDef): Component | undefined {
  const definition = COMPONENT_TYPE_DEFS.find(item => item.type === block.id)
  if (!definition) return undefined
  const wrapper = editor.getWrapper()
  if (!wrapper) return undefined

  if (definition.allowedParents.length === 0) {
    // 저장 스키마는 단일 루트 섹션을 사용하므로 이미 루트가 있으면 두 번째 섹션을 만들지 않는다.
    return wrapper.components().length === 0 ? wrapper : undefined
  }

  let candidate = editor.getSelected()
  while (candidate) {
    if (definition.allowedParents.some(type => candidate?.is(type))) return candidate
    candidate = candidate.parent()
  }

  // 첫 행은 캔버스 루트 섹션을 자동 대상으로 삼아 키보드만으로도 편집을 시작할 수 있다.
  const root = wrapper.getChildAt(0)
  return root && definition.allowedParents.some(type => root.is(type)) ? root : undefined
}

export function ScreenEditor() {
  const { uiId } = useParams<{ uiId: string }>()
  const navigate = useNavigate()
  const hostRef = useRef<HTMLDivElement>(null)
  const blocksRef = useRef<HTMLDivElement>(null)
  const traitsRef = useRef<HTMLDivElement>(null)
  const editorRef = useRef<Editor | null>(null)
  const queryCatalogRef = useRef<QueryCatalog | null>(null)
  const hydratingRef = useRef(false)
  const sourceRef = useRef<ScreenDefinitionSource>('missing')
  const purposeRef = useRef<ScreenPurpose>(SCREEN_PURPOSE_VALUES[0])
  const managePreviewModeRef = useRef<ManagePreviewMode>(DEFAULT_MANAGE_PREVIEW_MODE)
  // 디자이너에서 직접 편집하지 않는 화면 수준 설정을 보존해 재저장 시 유실하지 않는다.
  const flatRef = useRef<ScreenDefinitionDto | null>(null)
  const [title, setTitle] = useState('')
  const [purpose, setPurpose] = useState<ScreenPurpose>(SCREEN_PURPOSE_VALUES[0])
  const [managePreviewMode, setManagePreviewMode] = useState<ManagePreviewMode>(DEFAULT_MANAGE_PREVIEW_MODE)
  const [status, setStatus] = useState('초기화 중…')
  const [targetChannel, setTargetChannel] = useState<TargetChannel>('MES')
  const [entryPath, setEntryPath] = useState(uiId ? entryPathFor('MES', uiId) : '')
  const [ready, setReady] = useState(false)
  const [loadFailed, setLoadFailed] = useState(false)
  const [saving, setSaving] = useState(false)
  const [source, setSource] = useState<ScreenDefinitionSource>('missing')
  const [diagnostics, setDiagnostics] = useState<ScreenCapabilityDiagnostic[]>([])
  const [canImport, setCanImport] = useState(false)
  const [importing, setImporting] = useState(false)
  const [operationError, setOperationError] = useState<string | null>(null)
  const [canonicalUiId, setCanonicalUiId] = useState(uiId ?? '')
  const [dirty, setDirty] = useState(false)
  const [selectedComponent, setSelectedComponent] = useState<string | null>(null)
  const [draggingBlock, setDraggingBlock] = useState<string | null>(null)

  const canManage = hasPermission(getAccessToken(), 'sys:manage')
  const editable = ready && source === 'database' && !loadFailed
  const diagnosticErrors = diagnostics.filter(item => item.severity === 'Error')

  // 미리보기 모드는 저장 계약과 분리한다. 목적/선택값이 바뀔 때 현재 iframe에만 data 속성을 전달하며,
  // Manage가 아닌 화면은 항상 표준 보기로 되돌려 다른 화면 목적의 캔버스가 영향을 받지 않게 한다.
  useEffect(() => {
    purposeRef.current = purpose
    managePreviewModeRef.current = managePreviewMode
    try {
      const cdoc = editorRef.current?.Canvas.getDocument()
      if (!cdoc) return
      cdoc.documentElement.dataset.nxManagePreview = purpose === 'Manage'
        ? managePreviewMode
        : DEFAULT_MANAGE_PREVIEW_MODE
    } catch { /* iframe 준비 전 또는 테스트 환경의 접근 실패는 load 이벤트에서 다시 적용한다. */ }
  }, [purpose, managePreviewMode])

  // 코드 시드/미존재 화면은 포인터뿐 아니라 키보드 포커스도 캔버스·블록·trait에 들어가지 않게 잠근다.
  // 가져오기가 끝나 source=database가 되면 inert를 제거해 같은 Editor 인스턴스를 즉시 편집 가능 상태로 전환한다.
  useEffect(() => {
    for (const element of [hostRef.current, blocksRef.current, traitsRef.current]) {
      if (!element) continue
      if (editable) element.removeAttribute('inert')
      else element.setAttribute('inert', '')
    }
  }, [editable])

  function applyLoadedDefinition(editor: Editor, loaded: LoadedScreenDefinition) {
    flatRef.current = loaded.flat
    sourceRef.current = loaded.source
    setCanonicalUiId(loaded.canonicalUiId)
    setTitle(loaded.title || (uiId ?? ''))
    setPurpose(normalizeScreenPurpose(loaded.flat?.purpose))
    setManagePreviewMode(DEFAULT_MANAGE_PREVIEW_MODE)
    setTargetChannel(loaded.targetChannel)
    setEntryPath(loaded.entryPath)
    setSource(loaded.source)
    setDiagnostics(loaded.diagnostics)
    setCanImport(loaded.canImport)
    const effective: LayoutNode | null = loaded.layout
      ?? (loaded.flat ? flatToLayout(loaded.flat, queryCatalogRef.current ?? undefined) : null)
    const root: GrapesNode = effective
      ? layoutToComponent(effective)
      : { type: 'nx-section', attributes: { 'data-nx-component': 'nx-section' }, components: [] }
    hydratingRef.current = true
    editor.setComponents([root] as ComponentAdd)
    hydratingRef.current = false
    setDirty(false)
    setSelectedComponent(null)
    setReady(true)
  }

  useEffect(() => {
    if (!hostRef.current || !blocksRef.current || !traitsRef.current || !canManage) return
    let disposed = false
    setReady(false)
    setLoadFailed(false)
    setSource('missing')
    sourceRef.current = 'missing'
    hydratingRef.current = false
    setCanonicalUiId(uiId ?? '')
    setDiagnostics([])
    setCanImport(false)
    setOperationError(null)
    setDirty(false)
    setSelectedComponent(null)
    setDraggingBlock(null)
    queryCatalogRef.current = null
    setStatus('화면 정의 로딩 중…')
    // grapesjs EditorConfig는 우리 잠금 설정 형태와 정확히 호환되지 않아 init 경계에서만 캐스팅(설정은 grapesConfig 단위테스트가 보증).
    // 블록/트레이트 매니저는 전용 컨테이너(appendTo)에 마운트 — 기본 패널 비활성(panels.defaults=[]) 상태에서도 노출된다.
    const editor = grapesjs.init(buildEditorConfig(hostRef.current, blocksRef.current, traitsRef.current) as never)
    editorRef.current = editor

    // GrapesJS 캔버스는 iframe이라 부모의 theme/디자인 미리보기 상태를 상속하지 않는다. frameStyle은
    // buildEditorConfig가 주입하고, 여기서는 두 상태를 iframe documentElement에 실시간으로 전달한다.
    const applyCanvasPresentation = () => {
      try {
        const theme = document.documentElement.dataset.theme || 'light'
        const cdoc = editor.Canvas.getDocument()
        if (!cdoc) return
        cdoc.documentElement.dataset.theme = theme
        cdoc.documentElement.dataset.nxManagePreview = purposeRef.current === 'Manage'
          ? managePreviewModeRef.current
          : DEFAULT_MANAGE_PREVIEW_MODE
      } catch { /* 캔버스 접근 실패(테스트 jsdom 등) — 무시 */ }
    }
    editor.on('load', applyCanvasPresentation)
    applyCanvasPresentation()
    const themeObserver = new MutationObserver(applyCanvasPresentation)
    try {
      themeObserver.observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] })
    } catch { /* 관측 실패 — 진입 시 1회 적용으로 폴백 */ }

    listQueries()
      .then((cat: QueryCatalog) => {
        if (disposed) {
          return {
            canonicalUiId: uiId ?? '',
            title: '', layout: null as LayoutNode | null, flat: null,
            targetChannel: 'MES' as TargetChannel,
            entryPath: uiId ? entryPathFor('MES', uiId) : '',
            source: 'missing' as ScreenDefinitionSource,
            diagnostics: [],
            canImport: false,
          }
        }
        queryCatalogRef.current = cat
        const traits = buildTraitDefs(cat)
        for (const c of COMPONENT_TYPE_DEFS) {
          // 중첩 규칙은 문자열(CSS 셀렉터)이 아니라 type 기반 함수(toModelDefaults)로 줘야 드롭이 동작한다.
          editor.DomComponents.addType(c.type, {
            model: { defaults: toModelDefaults(c, traits[c.type] ?? []) },
          } as AddComponentTypeOptions)
        }
        for (const b of BLOCK_DEFS) {
          editor.BlockManager.add(b.id, {
            label: `<span class="nx-block-label"><strong>${b.label}</strong><small>${b.description}</small></span>`,
            category: b.category,
            content: markedBlockContent(b),
            select: true,
            attributes: {
              role: 'button', tabindex: '0', 'data-nx-block': b.id,
              'aria-label': `${b.label} 블록. ${b.description}. 캔버스로 드래그하거나 Enter 키로 추가`,
              title: `${b.description} · 드래그 또는 Enter로 추가`,
            },
            onClick: (_block, activeEditor) => {
              const target = findInsertionTarget(activeEditor, b)
              if (!target) {
                setStatus(`${b.label}을(를) 담을 상위 컴포넌트를 먼저 선택하세요.`)
                return
              }
              const [added] = target.append(markedBlockContent(b))
              if (added) activeEditor.select(added)
              setDirty(true)
              setStatus(`${b.label} 추가됨 — 저장하면 DB에 반영됩니다.`)
            },
          })
        }
        // 바인딩 선택/교체/해제 시 카탈로그의 권한을 node metadata에 즉시 반영한다.
        // helper가 현재 값과 비교하므로 자기 add/remove 이벤트가 다시 와도 무한 루프가 생기지 않는다.
        const markLayoutDirty = () => {
          if (hydratingRef.current || sourceRef.current !== 'database') return
          setDirty(true)
          setStatus('레이아웃 변경됨 — 저장하면 DB에 반영됩니다.')
        }
        const syncPermission = (component: Component) => {
          syncRequiredPermission(component, cat)
          markLayoutDirty()
        }
        editor.on('component:add', syncPermission)
        editor.on('component:update:attributes', syncPermission)
        editor.on('component:remove', markLayoutDirty)
        editor.on('component:drag:end', markLayoutDirty)
        editor.on('component:selected', (component: Component) => setSelectedComponent(describeComponent(component)))
        editor.on('component:deselected', () => setSelectedComponent(null))
        editor.on('block:drag:start', (block: { getId(): string }) => {
          const dragged = BLOCK_DEFS.find(item => item.id === block.getId())
          setDraggingBlock(dragged?.label ?? '블록')
        })
        editor.on('block:drag:stop', () => setDraggingBlock(null))

        // GrapesJS 블록은 기본적으로 drag 전용 div다. role/tabIndex는 Block attributes로 주고,
        // Enter/Space는 동일한 onClick 삽입 경로를 호출해 키보드 사용자에게도 실제 동작을 제공한다.
        const blockContainer = blocksRef.current
        const handleBlockKeyDown = (event: KeyboardEvent) => {
          if (event.key !== 'Enter' && event.key !== ' ') return
          const target = event.target instanceof Element
            ? event.target.closest<HTMLElement>('[data-nx-block]')
            : null
          if (!target) return
          event.preventDefault()
          target.click()
        }
        blockContainer?.addEventListener('keydown', handleBlockKeyDown)
        editor.on('destroy', () => blockContainer?.removeEventListener('keydown', handleBlockKeyDown))
        return uiId
          ? loadDefinition(uiId)
          : {
              canonicalUiId: '',
              title: '', layout: null as LayoutNode | null, flat: null,
              targetChannel: 'MES' as TargetChannel,
              entryPath: '',
              source: 'missing' as ScreenDefinitionSource,
              diagnostics: [],
              canImport: false,
            }
      })
      .then(loaded => {
        if (disposed) return
        // InMemory 시드 카탈로그는 별칭 조회에도 canonical UiId를 반환한다. alias URL 상태에서 import하면
        // alias를 다시 DB 조회하게 되므로 먼저 canonical 경로로 replace 이동해 이후 모든 저장/가져오기를 정규화한다.
        if (loaded.source === 'seed' && uiId && loaded.canonicalUiId !== uiId) {
          navigate(`/Designer/${encodeURIComponent(loaded.canonicalUiId)}`, { replace: true })
          return
        }
        applyLoadedDefinition(editor, loaded)
        if (loaded.source === 'database') {
          setStatus('DB 정의 · 편집 가능')
        } else if (loaded.source === 'seed') {
          setStatus(loaded.canImport
            ? '코드 시드 미리보기 · DB로 가져온 뒤 편집할 수 있습니다.'
            : '코드 시드 미리보기 · 검증 오류를 해결해야 가져올 수 있습니다.')
        } else {
          setStatus('등록된 화면 정의가 없습니다. 목록에서 신규 화면을 생성하세요.')
        }
      })
      .catch(() => {
        if (!disposed) {
          setLoadFailed(true)
          setReady(false)
          setStatus('로드 실패(권한/네트워크 확인)')
        }
      })

    return () => {
      disposed = true
      themeObserver.disconnect()
      queryCatalogRef.current = null
      editor.destroy()
      editorRef.current = null
    }
  }, [uiId, canManage, navigate])

  async function handleSave() {
    const editor = editorRef.current
    if (!editor || !uiId || !editable || saving) return
    try {
      setSaving(true)
      setOperationError(null)
      setStatus('저장 중…')
      await saveDefinition(uiId, title || uiId, readRootLayout(editor), {
        purpose,
        refreshIntervalSeconds: flatRef.current?.refreshIntervalSeconds,
        searchFields: flatRef.current?.searchFields,
        countQueryId: flatRef.current?.countQueryId,
        deleteQueryId: flatRef.current?.deleteQueryId,
        bulkCommands: flatRef.current?.bulkCommands,
        readRequiredPermission: flatRef.current?.readRequiredPermission,
        saveRequiredPermission: flatRef.current?.saveRequiredPermission,
        deleteRequiredPermission: flatRef.current?.deleteRequiredPermission,
        targetChannel,
        entryPath,
      })
      setStatus('저장됨')
      setDirty(false)
    } catch (error) {
      if (error instanceof Error && error.message.startsWith('화면 레이아웃 구조가 올바르지 않습니다:')) {
        setOperationError(error.message)
        setStatus('저장 실패(레이아웃 구조 확인)')
      } else if (error instanceof ApiError && error.status === 403) {
        setOperationError('화면 저장 권한(sys:manage)이 없습니다.')
        setStatus('저장 실패')
      } else {
        setOperationError('화면을 저장하지 못했습니다. 서버 연결과 화면 정의를 확인하세요.')
        setStatus('저장 실패')
      }
    } finally {
      setSaving(false)
    }
  }

  async function handleImport() {
    const editor = editorRef.current
    const importUiId = canonicalUiId || uiId
    if (!editor || !importUiId || source !== 'seed' || !canImport || importing) return
    if (!window.confirm(`${title || importUiId} 코드 시드를 DB에 가져오시겠습니까?\n기존 DB 정의는 덮어쓰지 않습니다.`)) return

    setImporting(true)
    setOperationError(null)
    setStatus('코드 시드를 DB에 가져오는 중…')
    try {
      await importScreenSeed(importUiId)
      const loaded = await loadDefinition(importUiId)
      if (loaded.source !== 'database') throw new Error('Imported definition was not returned from the database.')
      applyLoadedDefinition(editor, loaded)
      setStatus('DB로 가져왔습니다. 편집할 수 있습니다.')
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        try {
          const loaded = await loadDefinition(importUiId)
          if (loaded.source === 'database') {
            applyLoadedDefinition(editor, loaded)
            setStatus('이미 DB에 등록된 화면을 다시 불러왔습니다.')
            return
          }
        } catch { /* 아래 충돌 안내를 유지한다. */ }
        setOperationError('이미 DB에 등록된 화면입니다. 목록에서 다시 열어 주세요.')
      } else if (error instanceof ApiError && error.status === 422) {
        setOperationError('화면 목적과 기능이 맞지 않아 가져올 수 없습니다. capability 진단을 확인하세요.')
      } else if (error instanceof ApiError && error.status === 403) {
        setOperationError('화면 가져오기 권한(sys:manage)이 없습니다.')
      } else {
        setOperationError('코드 시드를 DB에 가져오지 못했습니다. 서버 연결을 확인하세요.')
      }
      setStatus('가져오기 실패')
    } finally {
      setImporting(false)
    }
  }

  function handleTargetChange(next: TargetChannel) {
    setTargetChannel(next)
    if (uiId) setEntryPath(entryPathFor(next, uiId))
    setDirty(true)
    setStatus('대상 채널 변경됨 — 저장하면 DB에 반영됩니다.')
  }

  function handlePurposeChange(next: string) {
    if (!SCREEN_PURPOSE_VALUES.includes(next as ScreenPurpose)) return
    setPurpose(next as ScreenPurpose)
    setDirty(true)
    setStatus('화면 목적 변경됨 — 저장하면 DB에 반영됩니다.')
  }

  function handleTitleChange(next: string) {
    setTitle(next)
    if (!editable) return
    setDirty(true)
    setStatus('화면 제목 변경됨 — 저장하면 DB에 반영됩니다.')
  }

  function confirmLeaveWithChanges(event: React.MouseEvent<HTMLAnchorElement>) {
    if (!dirty || window.confirm('저장하지 않은 변경사항이 있습니다. 이 페이지를 나가시겠습니까?')) return
    event.preventDefault()
  }

  useEffect(() => {
    if (!dirty) return
    const handleBeforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', handleBeforeUnload)
    return () => window.removeEventListener('beforeunload', handleBeforeUnload)
  }, [dirty])

  useEffect(() => {
    const handleShortcut = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.key.toLocaleLowerCase() !== 's') return
      event.preventDefault()
      if (editable && !saving) void handleSave()
    }
    window.addEventListener('keydown', handleShortcut)
    return () => window.removeEventListener('keydown', handleShortcut)
  })

  const statusTone = loadFailed || operationError || status.includes('실패')
    ? 'is-error'
    : saving || importing || status.includes('중…')
      ? 'is-busy'
      : dirty
        ? 'is-dirty'
        : status.includes('저장됨') || status.includes('가져왔')
          ? 'is-success'
          : ''

  if (!canManage) return <div className="nx-gate">화면 디자이너 권한(sys:manage)이 없습니다.</div>

  return (
    <div className="nx-designer">
      <header className="nx-designer-bar">
        <div className="nx-designer-bar-main">
          <div className="nx-designer-identity">
            <Link className="nx-designer-back" to="/Designer" aria-label="화면 목록으로 돌아가기" onClick={confirmLeaveWithChanges}>← 목록</Link>
            <span className="nx-brand">화면 디자이너<small>SCREEN DESIGNER</small></span>
          </div>
          <div className="nx-designer-title-field">
            <label htmlFor="designer-title">화면 제목</label>
            <input
              id="designer-title"
              className="nx-input"
              aria-label="화면 제목"
              value={title}
              onChange={e => handleTitleChange(e.target.value)}
              placeholder="화면 제목"
              disabled={!editable || saving}
            />
          </div>
          <div className="nx-designer-actions">
            <label className="nx-sr-only" htmlFor="designer-target">대상 채널</label>
            <select
              id="designer-target"
              className="nx-input nx-target-select"
              value={targetChannel}
              onChange={e => handleTargetChange(e.target.value as TargetChannel)}
              disabled={!editable || saving}
            >
              {TARGET_CHANNELS.map(channel => <option key={channel} value={channel}>{channel}</option>)}
            </select>
            {ready && source !== 'missing' && (
              <a className="nx-btn nx-btn-ghost nx-designer-runtime" href={entryPath} onClick={confirmLeaveWithChanges}>런타임</a>
            )}
            {source === 'seed' && (
              <button className="nx-btn nx-btn-blue" onClick={handleImport} disabled={!canImport || importing}>
                {importing ? '가져오는 중…' : 'DB로 가져와 편집'}
              </button>
            )}
            <button className="nx-btn nx-btn-teal nx-save-button" onClick={handleSave} disabled={!uiId || !editable || saving}>
              {saving ? '저장 중…' : '저장'}
              <kbd aria-hidden="true">Ctrl S</kbd>
            </button>
          </div>
        </div>
        <div className="nx-designer-context-bar">
          <span className="nx-uiid"><small>UI ID</small>{uiId ?? '(미지정)'}</span>
          <span className={`nx-designer-source nx-source-${source}`}>
            {source === 'database' ? '출처: DB' : source === 'seed' ? '출처: 코드 시드' : '출처: 없음'}
          </span>
          <span className="nx-entry-context" title={entryPath}>{entryPath}</span>
          <span
            className={`nx-status ${statusTone}`}
            role={loadFailed && !operationError ? 'alert' : 'status'}
            aria-live="polite"
          >
            <span className="nx-status-dot" aria-hidden="true" />{status}
          </span>
        </div>
      </header>
      {source === 'seed' && (
        <section
          className={`nx-designer-notice ${diagnosticErrors.length > 0 ? 'is-error' : 'is-advisory'}`}
          role={diagnosticErrors.length > 0 ? 'alert' : 'status'}
          aria-label="코드 시드 상태"
        >
          <strong>{diagnosticErrors.length > 0 ? '가져오기 전 검증 오류' : '읽기 전용 코드 시드 미리보기'}</strong>
          <span>
            {diagnosticErrors.length > 0
              ? '목적과 기능의 모순을 해결해야 DB로 가져올 수 있습니다.'
              : '원본 보호를 위해 제목·목적·채널·캔버스는 읽기 전용입니다. DB로 가져온 뒤 편집하세요.'}
          </span>
          {diagnostics.length > 0 && (
            <ul>
              {diagnostics.map(item => <li key={`${item.code}-${item.message}`}>{item.code}: {item.message}</li>)}
            </ul>
          )}
        </section>
      )}
      {source === 'missing' && ready && (
        <section className="nx-designer-notice is-error" role="alert">
          <strong>화면 정의를 찾을 수 없습니다.</strong>
          <span>빈 화면을 저장하지 않도록 편집과 저장을 잠갔습니다. 목록에서 신규 화면을 생성하세요.</span>
        </section>
      )}
      {operationError && <p className="nx-designer-operation-error" role="alert">{operationError}</p>}
      <div className={`nx-designer-body ${editable ? '' : 'is-readonly'} ${draggingBlock ? 'is-dragging' : ''}`}>
        <aside className="nx-panel nx-panel-blocks" aria-disabled={!editable} aria-labelledby="block-panel-title">
          <div className="nx-panel-head">
            <h2 id="block-panel-title">블록 팔레트</h2>
            <span>{BLOCK_DEFS.length}</span>
          </div>
          <p className="nx-panel-hint">블록을 캔버스로 드래그하거나, 상위 영역 선택 후 Enter로 추가하세요.</p>
          {draggingBlock && <p className="nx-drag-status" role="status">{draggingBlock} 배치 위치를 선택하세요.</p>}
          <div ref={blocksRef} className="nx-panel-scroll" aria-label="사용 가능한 컴포넌트 블록" />
        </aside>
        <section className="nx-canvas-stage" aria-labelledby="canvas-stage-title">
          <div className="nx-canvas-stage-head">
            <div><strong id="canvas-stage-title">페이지 캔버스</strong><span>선택한 컴포넌트는 오른쪽에서 설정합니다.</span></div>
            <span className={`nx-edit-mode ${editable ? 'is-editable' : ''}`}>{editable ? '편집 모드' : '미리보기'}</span>
          </div>
          <div ref={hostRef} className="nx-canvas" aria-label="화면 디자인 캔버스" aria-disabled={!editable} />
        </section>
        <aside className="nx-panel nx-panel-traits" aria-disabled={!editable} aria-labelledby="property-panel-title">
          <div className="nx-panel-head">
            <h2 id="property-panel-title">속성</h2>
          </div>
          <div className="nx-selection-summary" aria-live="polite">
            <span>현재 선택</span>
            <strong>{selectedComponent ?? '선택된 컴포넌트 없음'}</strong>
            <small>{selectedComponent ? '아래 항목을 수정하면 캔버스에 즉시 반영됩니다.' : '캔버스에서 컴포넌트를 선택해 세부 속성을 편집하세요.'}</small>
          </div>
          <div className="nx-screen-properties">
            <span className="nx-property-group-title">화면 설정</span>
            <label className="nx-screen-property-label" htmlFor="designer-purpose">화면 목적</label>
            <select
              id="designer-purpose"
              className="nx-input nx-purpose-select"
              value={purpose}
              onChange={e => handlePurposeChange(e.target.value)}
              disabled={!editable || saving}
            >
              {SCREEN_PURPOSE_VALUES.map(value => (
                <option key={value} value={value}>{SCREEN_PURPOSE_LABELS[value]}</option>
              ))}
            </select>
            <p className="nx-screen-property-help">런타임의 등록·관리·조회 방식을 명시합니다.</p>
            {purpose === 'Manage' && (
              <fieldset className="nx-manage-preview">
                <legend>관리 화면 디자인 검토</legend>
                <label className="nx-screen-property-label" htmlFor="designer-manage-preview">보기 모드 미리보기</label>
                <select
                  id="designer-manage-preview"
                  className="nx-input nx-purpose-select"
                  value={managePreviewMode}
                  onChange={event => setManagePreviewMode(event.target.value as ManagePreviewMode)}
                  disabled={!ready}
                  aria-describedby="designer-manage-preview-description designer-manage-preview-notice"
                >
                  {MANAGE_PREVIEW_MODES.map(mode => (
                    <option key={mode.value} value={mode.value}>{mode.label}</option>
                  ))}
                </select>
                <p id="designer-manage-preview-description" className="nx-screen-property-help">
                  {describeManagePreviewMode(managePreviewMode)}
                </p>
                <p id="designer-manage-preview-notice" className="nx-preview-only-notice">
                  <strong>미리보기 전용 · 저장되지 않음</strong>
                  실제 런타임의 표 밀도 등 개인 설정은 MES 화면에서 사용자별로 저장됩니다.
                </p>
              </fieldset>
            )}
          </div>
          <div className="nx-trait-group-head"><span>컴포넌트 설정</span><small>선택한 블록 기준</small></div>
          <div ref={traitsRef} className="nx-panel-scroll" aria-label="선택한 컴포넌트 속성" />
        </aside>
      </div>
    </div>
  )
}
