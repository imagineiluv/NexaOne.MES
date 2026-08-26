import type { LoginResponse } from '../api/auth'

export const PORTAL_SESSION_KEY = 'nexaone.portal.session.v1'

function isLoginResponse(value: unknown): value is LoginResponse {
  if (!value || typeof value !== 'object') return false
  const candidate = value as Partial<LoginResponse>
  return typeof candidate.accessToken === 'string'
    && typeof candidate.refreshToken === 'string'
    && typeof candidate.userId === 'string'
    && typeof candidate.userName === 'string'
    && typeof candidate.plantId === 'string'
    && Array.isArray(candidate.roles)
    && candidate.roles.every(role => typeof role === 'string')
    && typeof candidate.requirePasswordChange === 'boolean'
}

export function restorePortalSession(): LoginResponse | null {
  try {
    const raw = sessionStorage.getItem(PORTAL_SESSION_KEY)
    if (!raw) return null
    const parsed: unknown = JSON.parse(raw)
    if (isLoginResponse(parsed)) return parsed
  } catch {
    // 손상되거나 이전 버전인 세션은 로그인 화면으로 안전하게 폴백한다.
  }
  sessionStorage.removeItem(PORTAL_SESSION_KEY)
  return null
}

export function persistPortalSession(session: LoginResponse | null): void {
  if (session) sessionStorage.setItem(PORTAL_SESSION_KEY, JSON.stringify(session))
  else sessionStorage.removeItem(PORTAL_SESSION_KEY)
}
