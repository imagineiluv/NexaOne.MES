import { lazy, Suspense, useState } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { Login } from './features/Login'
import { Dashboard } from './features/Dashboard'
import { setSession as setClientSession } from './api/client'
import type { LoginResponse } from './api/auth'

// 디자이너 지연 로드 — GrapesJS(번들의 ~80%, ~1MB)를 /designer 진입 시에만 받는다.
// 미인증은 Navigate가 먼저 평가돼 청크를 아예 fetch하지 않는다(가드 요소 수준 유지).
const ScreenEditor = lazy(() =>
  import('./features/ScreenEditor').then(m => ({ default: m.ScreenEditor })))

function DesignerRoute({ session }: { session: LoginResponse | null }) {
  if (!session) return <Navigate to="/" replace />
  return (
    <Suspense fallback={<div className="nx-designer-loading">디자이너 로딩 중…</div>}>
      <ScreenEditor />
    </Suspense>
  )
}

// 라우트 트리(테스트 가능하도록 분리) — 세션은 상위에서 주입. 미인증 디자이너 접근은 로그인으로 폴백.
export function AppRoutes({ session, setSession }: {
  session: LoginResponse | null
  setSession: (s: LoginResponse | null) => void
}) {
  return (
    <Routes>
      <Route path="/" element={
        session
          ? <Dashboard session={session} onLogout={() => setSession(null)} />
          : <Login onLoggedIn={setSession} />
      } />
      <Route path="/designer/:uiId" element={<DesignerRoute session={session} />} />
      <Route path="/designer" element={<DesignerRoute session={session} />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

// 공존 데모: 동일 NexaOne.API(REST/SignalR/JWT) 위에서 동작하는 React Pro-Code SPA.
export function App() {
  const [session, setSession] = useState<LoginResponse | null>(null)
  // 로그인 성공 시 client 모듈 세션도 동기화(apiFetch Bearer 토큰 소스). 로그아웃 시 해제.
  const sync = (s: LoginResponse | null) => {
    if (s) setClientSession({ accessToken: s.accessToken, refreshToken: s.refreshToken, userId: s.userId })
    else setClientSession(null)
    setSession(s)
  }
  return <AppRoutes session={session} setSession={sync} />
}
