import { useState } from 'react'
import { Navigate, Route, Routes } from 'react-router-dom'
import { Login } from './features/Login'
import { Dashboard } from './features/Dashboard'
import { ScreenEditor } from './features/ScreenEditor'
import { setSession as setClientSession } from './api/client'
import type { LoginResponse } from './api/auth'

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
      <Route path="/designer/:uiId" element={session ? <ScreenEditor /> : <Navigate to="/" replace />} />
      <Route path="/designer" element={session ? <ScreenEditor /> : <Navigate to="/" replace />} />
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
