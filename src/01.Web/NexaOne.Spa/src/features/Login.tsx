import { useState, type FormEvent } from 'react'
import { login, type LoginResponse } from '../api/auth'
import { ApiError } from '../api/client'

export function Login({ onLoggedIn }: { onLoggedIn: (session: LoginResponse) => void }) {
  const [userId, setUserId] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

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
        <h1>NexaOne 디자이너<small>NEXAONE MES</small></h1>
        <input className="nx-input" placeholder="사용자 ID" value={userId} onChange={e => setUserId(e.target.value)} autoComplete="username" />
        <input className="nx-input" placeholder="비밀번호" type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" />
        <button className="nx-btn nx-btn-teal" type="submit" disabled={busy || !userId || !password}>{busy ? '로그인 중…' : '로그인'}</button>
        {error && <p className="nx-error">{error}</p>}
      </form>
    </div>
  )
}
