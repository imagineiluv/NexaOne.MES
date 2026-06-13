import { useState } from 'react'
import { Login } from './features/Login'
import { Dashboard } from './features/Dashboard'
import type { LoginResponse } from './api/auth'

// 공존 데모: 동일 NexaOne.API(REST/SignalR/JWT) 위에서 동작하는 React Pro-Code SPA.
export function App() {
  const [session, setSession] = useState<LoginResponse | null>(null)
  return session
    ? <Dashboard session={session} onLogout={() => setSession(null)} />
    : <Login onLoggedIn={setSession} />
}
