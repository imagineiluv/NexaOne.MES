import { useState, type FormEvent } from 'react'
import { login, type LoginResponse } from '../api/auth'
import { ApiError } from '../api/client'

export type LoginSurface = 'designer' | 'mobile' | 'pop'

const SURFACE_COPY: Record<LoginSurface, { title: string; eyebrow: string; description: string }> = {
  designer: { title: 'NexaOne 디자이너', eyebrow: 'SCREEN DESIGNER', description: '페이지를 선택하거나 새 화면을 디자인합니다.' },
  mobile: { title: 'NexaOne Mobile', eyebrow: 'MOBILE MES', description: '모바일 작업 화면에 로그인합니다.' },
  pop: { title: 'NexaOne POP', eyebrow: 'SHOP FLOOR KIOSK', description: '현장 키오스크 작업 화면에 로그인합니다.' },
}

export function Login({ onLoggedIn, surface = 'designer' }: {
  onLoggedIn: (session: LoginResponse) => void
  surface?: LoginSurface
}) {
  const [userId, setUserId] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const copy = SURFACE_COPY[surface]

  async function submit(e: FormEvent) {
    e.preventDefault()
    setBusy(true)
    setError(null)
    try {
      onLoggedIn(await login(userId, password))
    } catch (err) {
      setError(err instanceof ApiError && err.status === 401
        ? '자격 증명이 올바르지 않습니다.'
        : '로그인에 실패했습니다.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="nx-login-wrap">
      <form onSubmit={submit} className="nx-login-card">
        <h1>{copy.title}<small>{copy.eyebrow}</small></h1>
        <p className="nx-login-description">{copy.description}</p>
        {/* 명시 라벨(sr-only) — placeholder는 접근 가능한 이름이 아니다. 시각 디자인은 그대로 유지. */}
        <label className="nx-sr-only" htmlFor="login-user">사용자 ID</label>
        <input id="login-user" name="username" className="nx-input" placeholder="사용자 ID" value={userId}
               onChange={e => setUserId(e.target.value)} autoComplete="username" required />
        <label className="nx-sr-only" htmlFor="login-password">비밀번호</label>
        <input id="login-password" name="password" className="nx-input" placeholder="비밀번호" type="password" value={password}
               onChange={e => setPassword(e.target.value)} autoComplete="current-password" required />
        <button className="nx-btn nx-btn-teal" type="submit" disabled={busy || !userId || !password}>{busy ? '로그인 중…' : '로그인'}</button>
        {/* 높이를 고정해 실패 메시지가 나타나도 폼이 흔들리지 않게 하고, 새 오류는 즉시 낭독한다. */}
        <div className="nx-error-slot" aria-live="assertive">
          {error && <p className="nx-error" role="alert">{error}</p>}
        </div>
      </form>
    </div>
  )
}
