// NexaOne.Server 호출 공용 클라이언트(JWT Bearer). 개발은 빈 BASE + Vite 프록시, 운영은 VITE_API_BASE_URL.
const BASE = import.meta.env.VITE_API_BASE_URL ?? ''

export interface Session {
  accessToken: string
  refreshToken: string
  userId: string
}

let session: Session | null = null
export const setSession = (s: Session | null): void => { session = s }
export const getAccessToken = (): string | null => session?.accessToken ?? null

type RefreshedSessionListener = (session: Session | null) => void
let refreshedSessionListener: RefreshedSessionListener | null = null

// App이 refresh 결과를 sessionStorage와 화면 세션에 반영할 수 있게 한다. 구독 해제 함수를 반환한다.
export function subscribeRefreshedSession(listener: RefreshedSessionListener): () => void {
  refreshedSessionListener = listener
  return () => {
    if (refreshedSessionListener === listener) refreshedSessionListener = null
  }
}

export class ApiError extends Error {
  constructor(public readonly status: number, public readonly body: string) {
    super(`API ${status}`)
  }
}

function buildHeaders(init: RequestInit): Headers {
  const headers = new Headers(init.headers)
  headers.set('Content-Type', 'application/json')
  if (session) headers.set('Authorization', `Bearer ${session.accessToken}`)
  return headers
}

// 동시 401이 각자 갱신을 트리거하면 회전 토큰이 서로를 무효화한다 — C# ApiClient._refreshLock과
// 동일한 single-flight 의도: 진행 중인 갱신 Promise를 모듈 레벨에 공유하고 동시 호출자는 같은 것을 await한다.
let refreshInFlight: Promise<boolean> | null = null

// 액세스 토큰 만료 시 리프레시 토큰으로 1회 재발급(§19.2 회전). 구 액세스 토큰은 plantId 승계용으로 헤더에 싣는다.
async function doRefresh(): Promise<boolean> {
  if (session === null) return false
  const res = await fetch(`${BASE}/api/v1/auth/refresh`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${session.accessToken}` },
    body: JSON.stringify({ userId: session.userId, refreshToken: session.refreshToken }),
  })
  if (!res.ok) {
    session = null
    refreshedSessionListener?.(null)
    return false
  }
  const data = (await res.json()) as { accessToken: string; refreshToken: string }
  session = { ...session, accessToken: data.accessToken, refreshToken: data.refreshToken }
  refreshedSessionListener?.(session)
  return true
}

function tryRefresh(): Promise<boolean> {
  // 이미 갱신이 진행 중이면 그 Promise를 공유(동시 호출자 합류). 정착되면 다음 401 웨이브를 위해 비운다.
  if (refreshInFlight === null) {
    refreshInFlight = doRefresh().finally(() => { refreshInFlight = null })
  }
  return refreshInFlight
}

export async function apiFetch<T>(path: string, init: RequestInit = {}): Promise<T> {
  let res = await fetch(`${BASE}${path}`, { ...init, headers: buildHeaders(init) })

  // refresh-on-401: 만료면 토큰 갱신 후 한 번 재시도(Phase 2)
  if (res.status === 401 && (await tryRefresh())) {
    res = await fetch(`${BASE}${path}`, { ...init, headers: buildHeaders(init) })
  }

  if (!res.ok) {
    const body = await res.text().catch(() => '')
    throw new ApiError(res.status, body)
  }
  if (res.status === 204) return undefined as T
  return (await res.json()) as T
}
