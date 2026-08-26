import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { LoginResponse } from '../api/auth'
import { ApiError } from '../api/client'
import {
  TARGET_CHANNELS,
  createDefinition,
  importScreenSeed,
  listDefinitions,
  listScreenSeeds,
  previewScreenSeed,
  type ScreenDefinitionSummary,
  type ScreenSeedSummary,
  type TargetChannel,
} from '../designer/api'

const TARGET_LABEL: Record<TargetChannel, string> = {
  MES: 'MES 데스크톱',
  MOBILE: 'Mobile / PDA',
  POP: 'POP / 키오스크',
}

const PURPOSE_LABEL: Record<string, string> = {
  Auto: '자동 판단',
  Register: '등록',
  Manage: '관리',
  Inquiry: '조회',
  Report: '현황·리포트',
  Execute: '작업 실행',
}

function formatUpdatedAt(value: string | null): string | null {
  if (!value) return null
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return null
  return new Intl.DateTimeFormat('ko-KR', {
    year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit',
  }).format(date)
}

function validateUiId(value: string): string | null {
  if (!value) return 'UI ID를 입력하세요.'
  if (!/^[A-Z0-9][A-Z0-9_.-]{0,99}$/.test(value)) {
    return 'UI ID는 영문 대문자·숫자로 시작하고 영문 대문자, 숫자, _, -, .만 사용할 수 있습니다.'
  }
  return null
}

export function DesignerHome({ session, onLogout }: { session: LoginResponse; onLogout: () => void }) {
  const navigate = useNavigate()
  const searchRef = useRef<HTMLInputElement>(null)
  const [screens, setScreens] = useState<ScreenDefinitionSummary[]>([])
  const [seeds, setSeeds] = useState<ScreenSeedSummary[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [query, setQuery] = useState('')
  const [channelFilter, setChannelFilter] = useState<TargetChannel | 'ALL'>('ALL')
  const [showCreate, setShowCreate] = useState(false)
  const [uiId, setUiId] = useState('')
  const [title, setTitle] = useState('')
  const [targetChannel, setTargetChannel] = useState<TargetChannel>('MES')
  const [createError, setCreateError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [importingUiId, setImportingUiId] = useState<string | null>(null)
  const [importError, setImportError] = useState<string | null>(null)
  const [seedsExpanded, setSeedsExpanded] = useState(false)
  const [reloadToken, setReloadToken] = useState(0)

  useEffect(() => {
    let disposed = false
    setLoading(true)
    setLoadError(null)
    Promise.all([listDefinitions(), listScreenSeeds()])
      .then(([registered, codeSeeds]) => {
        if (disposed) return
        setScreens(registered)
        setSeeds(codeSeeds)
      })
      .catch(() => {
        if (!disposed) setLoadError('화면 목록을 불러오지 못했습니다.')
      })
      .finally(() => {
        if (!disposed) setLoading(false)
    })
    return () => { disposed = true }
  }, [reloadToken])

  useEffect(() => {
    const focusSearch = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.key.toLocaleLowerCase() !== 'k') return
      event.preventDefault()
      searchRef.current?.focus({ preventScroll: true })
      searchRef.current?.select()
    }
    window.addEventListener('keydown', focusSearch)
    return () => window.removeEventListener('keydown', focusSearch)
  }, [])

  const filteredScreens = useMemo(() => {
    const term = query.trim().toLocaleLowerCase()
    return screens.filter(screen => {
      const matchesChannel = channelFilter === 'ALL' || screen.targetChannel === channelFilter
      const matchesTerm = !term
        || screen.uiId.toLocaleLowerCase().includes(term)
        || screen.title.toLocaleLowerCase().includes(term)
      return matchesChannel && matchesTerm
    })
  }, [channelFilter, query, screens])

  const filteredSeeds = useMemo(() => {
    const term = query.trim().toLocaleLowerCase()
    return seeds.filter(seed => {
      if (seed.databaseExists) return false
      const matchesChannel = channelFilter === 'ALL' || seed.targetChannel === channelFilter
      const matchesTerm = !term
        || seed.uiId.toLocaleLowerCase().includes(term)
        || seed.title.toLocaleLowerCase().includes(term)
      return matchesChannel && matchesTerm
    })
  }, [channelFilter, query, seeds])

  // 수백 개의 시드 카드를 첫 진입부터 모두 렌더링하지 않는다. 검색 중에는 일치 결과를 전부 보여 주고,
  // 평상시에는 12개만 노출해 Designer 첫 화면의 스캔 속도와 DOM 크기를 안정적으로 유지한다.
  const visibleSeeds = query.trim() || seedsExpanded ? filteredSeeds : filteredSeeds.slice(0, 12)
  const availableSeedCount = seeds.filter(seed => !seed.databaseExists).length
  const filterActive = query.trim().length > 0 || channelFilter !== 'ALL'

  async function handleCreate(e: FormEvent) {
    e.preventDefault()
    const normalizedUiId = uiId.trim().toUpperCase()
    const idError = validateUiId(normalizedUiId)
    if (idError) {
      setCreateError(idError)
      return
    }
    if (!title.trim()) {
      setCreateError('화면 제목을 입력하세요.')
      return
    }
    if (screens.some(screen => screen.uiId.toUpperCase() === normalizedUiId)) {
      setCreateError('이미 등록된 UI ID입니다. 목록에서 편집을 선택하세요.')
      return
    }
    if (seeds.some(seed => seed.uiId.toUpperCase() === normalizedUiId)) {
      setCreateError('같은 UI ID의 코드 시드가 있습니다. 코드 화면 가져오기를 사용하세요.')
      return
    }

    setCreating(true)
    setCreateError(null)
    try {
      // 목록은 canonical ID만 포함하므로 별칭(alias)은 로컬 중복검사로 잡히지 않는다. 서버 상세 조회가
      // 404일 때만 실제 신규 ID로 인정해 코드 시드를 빈 DB 정의로 가리는 사고를 막는다.
      try {
        const seed = await previewScreenSeed(normalizedUiId)
        setCreateError(`코드 시드 별칭 또는 UI ID입니다. '${seed.uiId}' 화면을 DB로 가져오세요.`)
        return
      } catch (error) {
        if (!(error instanceof ApiError) || error.status !== 404) {
          setCreateError('코드 시드 존재 여부를 확인하지 못해 생성을 중단했습니다. 서버 연결과 권한을 확인하세요.')
          return
        }
      }

      try {
        await createDefinition(normalizedUiId, title.trim(), targetChannel)
        navigate(`/Designer/${encodeURIComponent(normalizedUiId)}`)
      } catch (error) {
        setCreateError(error instanceof ApiError && error.status === 403
          ? '화면 생성 권한(sys:manage)이 없습니다.'
          : '화면을 생성하지 못했습니다. UI ID와 서버 연결을 확인하세요.')
      }
    } finally {
      setCreating(false)
    }
  }

  async function handleImport(seed: ScreenSeedSummary) {
    if (!seed.canImport || importingUiId) return
    const confirmed = window.confirm(
      `${seed.title} (${seed.uiId}) 코드 시드를 DB에 가져오시겠습니까?\n기존 DB 정의는 덮어쓰지 않습니다.`,
    )
    if (!confirmed) return

    setImportingUiId(seed.uiId)
    setImportError(null)
    try {
      await importScreenSeed(seed.uiId)
      navigate(`/Designer/${encodeURIComponent(seed.uiId)}`)
    } catch (error) {
      if (error instanceof ApiError && error.status === 409) {
        setImportError('이미 DB에 등록된 화면입니다. 목록을 새로고침한 뒤 편집하세요.')
      } else if (error instanceof ApiError && error.status === 422) {
        setImportError('화면 목적과 기능이 맞지 않아 가져올 수 없습니다. 진단 내용을 확인하세요.')
      } else if (error instanceof ApiError && error.status === 403) {
        setImportError('화면 가져오기 권한(sys:manage)이 없습니다.')
      } else {
        setImportError('코드 화면을 DB에 가져오지 못했습니다. 서버 연결을 확인하세요.')
      }
    } finally {
      setImportingUiId(null)
    }
  }

  return (
    <main className="nx-designer-home">
      <header className="nx-designer-home-bar">
        <div className="nx-designer-home-title">
          <h1>페이지 디자인</h1>
          <p>MES·PDA·POP 화면을 등록하고 컴포넌트 단위로 구성합니다.</p>
        </div>
        <div className="nx-designer-user">
          <span className="nx-designer-user-chip"><strong>{session.userName}</strong><small>{session.plantId}</small></span>
          <button type="button" className="nx-btn nx-btn-ghost" onClick={onLogout}>로그아웃</button>
        </div>
      </header>

      <section className="nx-designer-overview" aria-label="화면 자산 요약">
        <article className="nx-overview-card">
          <span>DB 등록 화면</span>
          <strong>{loading ? '—' : screens.length}</strong>
          <small>디자이너에서 바로 편집 가능</small>
        </article>
        <article className="nx-overview-card">
          <span>가져올 코드 화면</span>
          <strong>{loading ? '—' : availableSeedCount}</strong>
          <small>DB 등록 후 편집 가능</small>
        </article>
        <article className="nx-overview-card">
          <span>대상 채널</span>
          <strong>{TARGET_CHANNELS.length}</strong>
          <small>MES · Mobile · POP</small>
        </article>
      </section>

      <div className="nx-designer-toolbar-shell">
        <section className="nx-designer-toolbar" aria-label="화면 목록 도구">
          <div className="nx-screen-search-wrap">
            <label className="nx-sr-only" htmlFor="screen-search">화면 검색</label>
            <input
              ref={searchRef}
              id="screen-search"
              className="nx-input nx-screen-search"
              type="search"
              placeholder="UI ID 또는 화면 제목 검색"
              aria-keyshortcuts="Control+K Meta+K"
              value={query}
              onChange={e => setQuery(e.target.value)}
            />
            {query && (
              <button type="button" className="nx-search-clear" onClick={() => setQuery('')} aria-label="검색어 지우기">
                지우기
              </button>
            )}
            {!query && <kbd className="nx-search-shortcut" aria-hidden="true">Ctrl K</kbd>}
          </div>
          <label className="nx-sr-only" htmlFor="channel-filter">대상 채널 필터</label>
          <select
            id="channel-filter"
            className="nx-input nx-channel-filter"
            value={channelFilter}
            onChange={e => setChannelFilter(e.target.value as TargetChannel | 'ALL')}
          >
            <option value="ALL">전체 대상</option>
            {TARGET_CHANNELS.map(channel => <option key={channel} value={channel}>{TARGET_LABEL[channel]}</option>)}
          </select>
          <span className="nx-filter-result" aria-live="polite">
            {filterActive ? `${filteredScreens.length + filteredSeeds.length}개 결과` : '전체 화면'}
          </span>
          <button
            type="button"
            className="nx-btn nx-btn-teal nx-create-toggle"
            onClick={() => { setShowCreate(value => !value); setCreateError(null) }}
            aria-expanded={showCreate}
            aria-controls="create-screen-panel"
            aria-label={showCreate ? '신규 화면 닫기' : '신규 화면'}
          >
            {showCreate ? '신규 화면 닫기' : '+ 신규 화면'}
          </button>
        </section>
      </div>

      {showCreate && (
        <section id="create-screen-panel" className="nx-create-screen nx-card" aria-labelledby="create-screen-title">
          <div className="nx-create-screen-head">
            <div>
              <h2 id="create-screen-title">신규 페이지 생성</h2>
              <p>식별자와 대상 채널을 먼저 정한 뒤 편집기에서 위젯을 배치합니다.</p>
            </div>
            <button type="button" className="nx-btn nx-btn-ghost" onClick={() => { setShowCreate(false); setCreateError(null) }}>취소</button>
          </div>
          <form onSubmit={handleCreate}>
            <div className="nx-create-field nx-create-field-id">
              <label className="is-required" htmlFor="new-ui-id">UI ID</label>
              <input
                id="new-ui-id"
                className="nx-input"
                value={uiId}
                onChange={e => setUiId(e.target.value.toUpperCase())}
                placeholder="예: POM_MOBILE_WORK_EXECUTION"
                maxLength={100}
                autoComplete="off"
                aria-describedby="new-ui-id-help"
                required
                autoFocus
              />
              <small id="new-ui-id-help">영문 대문자·숫자·점·밑줄·하이픈을 사용할 수 있습니다.</small>
            </div>
            <div className="nx-create-field">
              <label className="is-required" htmlFor="new-title">화면 제목</label>
              <input
                id="new-title"
                className="nx-input"
                value={title}
                onChange={e => setTitle(e.target.value)}
                placeholder="작업자가 알아보기 쉬운 화면명"
                maxLength={200}
                required
              />
              <small>메뉴와 페이지 머리글에 표시됩니다.</small>
            </div>
            <div className="nx-create-field">
              <label htmlFor="new-target">대상 채널</label>
              <select
                id="new-target"
                className="nx-input"
                value={targetChannel}
                onChange={e => setTargetChannel(e.target.value as TargetChannel)}
              >
                {TARGET_CHANNELS.map(channel => <option key={channel} value={channel}>{TARGET_LABEL[channel]}</option>)}
              </select>
              <small>선택한 채널에 맞춰 런타임 경로가 생성됩니다.</small>
            </div>
            <div className="nx-create-actions">
              <button type="submit" className="nx-btn nx-btn-teal" disabled={creating}>
                {creating ? '생성 중…' : '생성 후 편집'}
              </button>
            </div>
          </form>
          {createError && <p className="nx-error" role="alert">{createError}</p>}
        </section>
      )}

      <section className="nx-screen-section" aria-labelledby="screen-list-title">
        <div className="nx-screen-section-head">
          <div>
            <h2 id="screen-list-title">등록 화면</h2>
            <p>저장된 화면을 편집하거나 실제 런타임에서 바로 확인합니다.</p>
          </div>
          <span className="nx-count-badge">{filteredScreens.length}개</span>
        </div>
        {loading && <p className="nx-muted" role="status">화면 목록을 불러오는 중…</p>}
        {loadError && (
          <div className="nx-load-error" role="alert">
            <div><strong>화면 자산을 불러오지 못했습니다.</strong><span>{loadError}</span></div>
            <button type="button" className="nx-btn nx-btn-ghost" onClick={() => setReloadToken(value => value + 1)}>다시 시도</button>
          </div>
        )}
        {!loading && !loadError && filteredScreens.length === 0 && (
          <div className="nx-empty-state">
            <strong>조건에 맞는 화면이 없습니다.</strong>
            <span>검색 조건을 바꾸거나 신규 화면을 생성하세요.</span>
          </div>
        )}
        {!loading && !loadError && filteredScreens.length > 0 && (
          <ul className="nx-screen-grid">
            {filteredScreens.map(screen => (
              <li key={screen.uiId} className="nx-screen-card nx-card">
                <div className="nx-screen-card-main">
                  <div className="nx-screen-card-badges">
                    <span className="nx-origin-badge nx-origin-database">출처: DB</span>
                    <span className={`nx-channel-badge nx-channel-${screen.targetChannel.toLocaleLowerCase()}`}>{screen.targetChannel}</span>
                  </div>
                  <h3>{screen.title}</h3>
                  <code>{screen.uiId}</code>
                  <span className="nx-entry-path">{screen.entryPath}</span>
                  {formatUpdatedAt(screen.updatedAt) && (
                    <time className="nx-screen-updated" dateTime={screen.updatedAt ?? undefined}>
                      최근 수정 {formatUpdatedAt(screen.updatedAt)}
                    </time>
                  )}
                </div>
                <div className="nx-screen-card-actions">
                  <Link
                    className="nx-btn nx-btn-teal"
                    to={`/Designer/${encodeURIComponent(screen.uiId)}`}
                    aria-label={`${screen.title} 편집`}
                  >편집</Link>
                  <a className="nx-btn nx-btn-ghost" href={screen.entryPath} aria-label={`${screen.title} 런타임 열기`}>런타임</a>
                </div>
              </li>
            ))}
          </ul>
        )}
      </section>

      <section className="nx-screen-section nx-seed-section" aria-labelledby="seed-list-title">
        <div className="nx-screen-section-head">
          <div>
            <h2 id="seed-list-title">코드 화면 가져오기</h2>
            <p>코드에 포함된 원본 화면을 확인한 뒤 DB에 등록해야 편집할 수 있습니다.</p>
          </div>
          <span className="nx-count-badge">{filteredSeeds.length}개</span>
        </div>
        {importingUiId && <p className="nx-muted" role="status">{importingUiId} 화면을 DB에 가져오는 중…</p>}
        {importError && <p className="nx-error nx-seed-message" role="alert">{importError}</p>}
        {!loading && !loadError && filteredSeeds.length === 0 && (
          <div className="nx-empty-state">
            <strong>가져올 코드 화면이 없습니다.</strong>
            <span>검색·채널 조건을 바꾸거나 모든 코드 시드가 이미 DB에 등록되었는지 확인하세요.</span>
          </div>
        )}
        {!loading && !loadError && filteredSeeds.length > 0 && (
          <ul className="nx-screen-grid">
            {visibleSeeds.map(seed => {
              const hasErrors = seed.errorCount > 0 || seed.diagnostics.some(item => item.severity === 'Error')
              const hasAdvisory = seed.advisoryCount > 0 || seed.diagnostics.some(item => item.severity === 'Advisory')
              return (
                <li key={seed.uiId} className="nx-screen-card nx-seed-card nx-card">
                  <div className="nx-screen-card-main">
                    <div className="nx-screen-card-badges">
                      <span className="nx-origin-badge nx-origin-seed">출처: 코드 시드</span>
                      <span className={`nx-channel-badge nx-channel-${seed.targetChannel.toLocaleLowerCase()}`}>{seed.targetChannel}</span>
                      <span className="nx-purpose-badge">목적: {PURPOSE_LABEL[seed.purpose] ?? seed.purpose}</span>
                    </div>
                    <h3>{seed.title}</h3>
                    <code>{seed.uiId}</code>
                    <span className="nx-entry-path">{seed.entryPath}</span>
                    {hasErrors && (
                      <span className="nx-capability-message is-error" role="alert">
                        검증 오류 {seed.errorCount || seed.diagnostics.filter(item => item.severity === 'Error').length}건 · 가져오기 불가
                      </span>
                    )}
                    {!hasErrors && hasAdvisory && (
                      <span className="nx-capability-message is-advisory">
                        확인 필요: 화면 목적 Auto · 가져온 뒤 명시적 목적을 선택하세요.
                      </span>
                    )}
                  </div>
                  <div className="nx-screen-card-actions">
                    <Link
                      className="nx-btn nx-btn-ghost"
                      to={`/Designer/${encodeURIComponent(seed.uiId)}`}
                      aria-label={`${seed.title} 코드 시드 미리보기`}
                    >미리보기</Link>
                    <button
                      type="button"
                      className="nx-btn nx-btn-teal"
                      onClick={() => handleImport(seed)}
                      disabled={!seed.canImport || importingUiId !== null}
                      aria-label={`${seed.title} DB로 가져오기`}
                      title={hasErrors ? 'capability 검증 오류를 해결해야 가져올 수 있습니다.' : undefined}
                    >{importingUiId === seed.uiId ? '가져오는 중…' : 'DB로 가져오기'}</button>
                  </div>
                </li>
              )
            })}
          </ul>
        )}
        {!loading && !loadError && !query.trim() && filteredSeeds.length > 12 && (
          <div className="nx-seed-more">
            <button
              type="button"
              className="nx-btn nx-btn-ghost"
              onClick={() => setSeedsExpanded(value => !value)}
              aria-expanded={seedsExpanded}
            >
              {seedsExpanded ? '코드 화면 접기' : `코드 화면 더 보기 (${filteredSeeds.length - 12}개)`}
            </button>
          </div>
        )}
      </section>
    </main>
  )
}
