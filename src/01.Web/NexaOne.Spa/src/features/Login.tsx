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
    <form onSubmit={submit} style={{ maxWidth: 320, margin: '12vh auto', display: 'grid', gap: 8, fontFamily: 'sans-serif' }}>
      <h1>NexaMes — React Pro-Code</h1>
      <input placeholder="사용자 ID" value={userId} onChange={e => setUserId(e.target.value)} autoComplete="username" />
      <input placeholder="비밀번호" type="password" value={password} onChange={e => setPassword(e.target.value)} autoComplete="current-password" />
      <button type="submit" disabled={busy || !userId || !password}>{busy ? '로그인 중…' : '로그인'}</button>
      {error && <p style={{ color: 'crimson' }}>{error}</p>}
    </form>
  )
}
