// NexaOne.API 호출 공용 클라이언트(ADR-003 JWT Bearer). 개발은 빈 BASE + Vite 프록시, 운영은 VITE_API_BASE_URL.
const BASE = import.meta.env.VITE_API_BASE_URL ?? ''

let accessToken: string | null = null
export const setAccessToken = (token: string | null): void => { accessToken = token }
export const getAccessToken = (): string | null => accessToken

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly body: string) {
    super(`API ${status}`)
  }
}

export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers)
  headers.set('Content-Type', 'application/json')
  if (accessToken) headers.set('Authorization', `Bearer ${accessToken}`)

  const res = await fetch(`${BASE}${path}`, { ...init, headers })
  if (!res.ok) {
    const body = await res.text().catch(() => '')
    throw new ApiError(res.status, body)
  }
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}
