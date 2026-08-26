import { lazy, Suspense, useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { Navigate, Route, Routes, useParams } from 'react-router-dom'
import { Login } from './pages/Login'
import { DesignerHome } from './pages/DesignerHome'
import { setSession as setClientSession, subscribeRefreshedSession } from './api/client'
import { logout, type LoginResponse } from './api/auth'
import { hasPermission } from './auth/jwt'
import { persistPortalSession, restorePortalSession } from './auth/session'

// 디자이너 지연 로드 — GrapesJS(번들의 ~80%, ~1MB)를 /designer 진입 시에만 받는다.
// 미인증은 Navigate가 먼저 평가돼 청크를 아예 fetch하지 않는다(가드 요소 수준 유지).
const ScreenEditor = lazy(() =>
  import('./pages/ScreenEditor').then(m => ({ default: m.ScreenEditor })))

function DesignerAccess({ session, setSession, children }: {
  session: LoginResponse | null
  setSession: (s: LoginResponse | null) => void
  children: ReactNode
}) {
  // 로그인 화면을 현재 URL에서 렌더해 /Designer/:uiId 목적 경로를 잃지 않는다.
  if (!session) return <Login surface="designer" onLoggedIn={setSession} />
  if (!hasPermission(session.accessToken, 'sys:manage')) {
    return (
      <main className="nx-access-denied">
        <h1>디자이너 접근 권한이 없습니다.</h1>
        <p>화면 디자인에는 <code>sys:manage</code> 권한이 필요합니다.</p>
        <button type="button" className="nx-btn nx-btn-ghost" onClick={() => { logout(); setSession(null) }}>로그아웃</button>
      </main>
    )
  }
  return children
}

function DesignerEditorRoute({ session, setSession }: {
  session: LoginResponse | null
  setSession: (s: LoginResponse | null) => void
}) {
  return (
    <DesignerAccess session={session} setSession={setSession}>
      <Suspense fallback={<div className="nx-designer-loading">디자이너 로딩 중…</div>}>
        <ScreenEditor />
      </Suspense>
    </DesignerAccess>
  )
}

function LegacyDesignerEditorRedirect() {
  const { uiId } = useParams<{ uiId: string }>()
  return <Navigate to={uiId ? `/Designer/${encodeURIComponent(uiId)}` : '/Designer'} replace />
}

// 라우트 트리(테스트 가능하도록 분리) — 세션은 상위에서 주입. 미인증 디자이너 접근은 로그인으로 폴백.
export function AppRoutes({ session, setSession }: {
  session: LoginResponse | null
  setSession: (s: LoginResponse | null) => void
}) {
  return (
    <Routes>
      <Route path="/" element={<Navigate to="/Designer" replace />} />
      <Route path="/Designer" element={
        <DesignerAccess session={session} setSession={setSession}>
          {session && <DesignerHome session={session} onLogout={() => { logout(); setSession(null) }} />}
        </DesignerAccess>
      } />
      <Route path="/Designer/:uiId" element={<DesignerEditorRoute session={session} setSession={setSession} />} />

      {/* /spa 공개 경로는 북마크 하위호환만 유지하고 정식 /Designer로 수렴한다. */}
      <Route path="/spa" element={<Navigate to="/Designer" replace />} />
      <Route path="/spa/designer" element={<Navigate to="/Designer" replace />} />
      <Route path="/spa/designer/:uiId" element={<LegacyDesignerEditorRedirect />} />
      <Route path="/spa/*" element={<Navigate to="/Designer" replace />} />
      <Route path="*" element={<Navigate to="/Designer" replace />} />
    </Routes>
  )
}

// Server가 /spa로 제공하며 동일 REST/SignalR/JWT 계약을 사용하는 React Portal client.
export function App() {
  const [session, setSession] = useState<LoginResponse | null>(() => {
    const restored = restorePortalSession()
    if (restored) {
      setClientSession({ accessToken: restored.accessToken, refreshToken: restored.refreshToken, userId: restored.userId })
    }
    return restored
  })
  const sessionRef = useRef(session)

  // 로그인·로그아웃과 refresh 회전을 client 메모리/React/sessionStorage 세 곳에 원자적으로 반영한다.
  const sync = useCallback((next: LoginResponse | null) => {
    sessionRef.current = next
    persistPortalSession(next)
    if (next) setClientSession({ accessToken: next.accessToken, refreshToken: next.refreshToken, userId: next.userId })
    else setClientSession(null)
    setSession(next)
  }, [])

  useEffect(() => subscribeRefreshedSession(refreshed => {
    const current = sessionRef.current
    if (!refreshed || !current) {
      sync(null)
      return
    }
    sync({ ...current, accessToken: refreshed.accessToken, refreshToken: refreshed.refreshToken })
  }), [sync])

  /* 로그인 성공 시 client 모듈 세션도 동기화(apiFetch Bearer 토큰 소스). 로그아웃 시 영속 세션까지 해제. */
  const handleSession = (next: LoginResponse | null) => {
    sync(next)
  }
  return <AppRoutes session={session} setSession={handleSession} />
}
