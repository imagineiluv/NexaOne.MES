import { beforeEach, describe, expect, it } from 'vitest'
import type { LoginResponse } from '../../api/auth'
import { PORTAL_SESSION_KEY, persistPortalSession, restorePortalSession } from '../session'

const session: LoginResponse = {
  accessToken: 'access',
  refreshToken: 'refresh',
  userId: 'admin',
  userName: '관리자',
  plantId: 'DEFAULT',
  roles: ['ADMIN'],
  requirePasswordChange: false,
}

describe('Portal 세션 저장소', () => {
  beforeEach(() => sessionStorage.clear())

  it('로그인 세션을 sessionStorage에 저장하고 새로고침 시 복원', () => {
    persistPortalSession(session)
    expect(restorePortalSession()).toEqual(session)
  })

  it('로그아웃은 영속 세션을 제거', () => {
    persistPortalSession(session)
    persistPortalSession(null)
    expect(sessionStorage.getItem(PORTAL_SESSION_KEY)).toBeNull()
    expect(restorePortalSession()).toBeNull()
  })

  it('손상되거나 계약이 다른 값은 제거하고 미인증으로 폴백', () => {
    sessionStorage.setItem(PORTAL_SESSION_KEY, JSON.stringify({ accessToken: 'only-token' }))
    expect(restorePortalSession()).toBeNull()
    expect(sessionStorage.getItem(PORTAL_SESSION_KEY)).toBeNull()
  })
})
