import { apiFetch, setAccessToken } from './client'

// NexaOne.API의 LoginResponse 계약(camelCase). 정식 타입은 `npm run gen:api`로 OpenAPI에서 생성 가능.
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
  const res = await apiFetch<LoginResponse>('/api/v1/auth/login', {
    method: 'POST',
    body: JSON.stringify({ userId, password, plantId }),
  })
  setAccessToken(res.accessToken)
  return res
}

export function logout(): void {
  setAccessToken(null)
}
