import { apiFetch, setSession } from './client'

// NexaOne.Server의 LoginResponse 계약(camelCase). 정식 타입은 `npm run gen:api`로 OpenAPI에서 생성 가능.
export interface LoginResponse {
  accessToken: string
  refreshToken: string
  userId: string
  userName: string
  plantId: string
  roles: string[]
  requirePasswordChange: boolean
}

export async function login(userId: string, password: string, plantId = 'DEFAULT'): Promise<LoginResponse> {
  // 로그인은 익명 진입점(토큰 없이 호출) — 성공 후 세션(액세스/리프레시/사용자) 저장
  const res = await apiFetch<LoginResponse>('/api/v1/auth/login', {
    method: 'POST',
    body: JSON.stringify({ userId, password, plantId }),
  })
  setSession({ accessToken: res.accessToken, refreshToken: res.refreshToken, userId: res.userId })
  return res
}

export function logout(): void {
  setSession(null)
}
