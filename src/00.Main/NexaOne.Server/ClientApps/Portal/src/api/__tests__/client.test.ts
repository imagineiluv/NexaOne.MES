import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiFetch, setSession, subscribeRefreshedSession } from '../client'

const fetchMock = vi.fn()

function json(body: unknown, status = 200): Promise<Response> {
  return Promise.resolve(new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  }))
}

describe('API client refresh 세션 동기화', () => {
  beforeEach(() => {
    fetchMock.mockReset()
    vi.stubGlobal('fetch', fetchMock)
    setSession({ accessToken: 'expired', refreshToken: 'refresh-1', userId: 'admin' })
  })

  afterEach(() => {
    setSession(null)
    vi.unstubAllGlobals()
  })

  it('401 refresh 성공 시 회전된 토큰을 구독자에게 전달하고 원 요청을 재시도', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockReturnValueOnce(json({ accessToken: 'access-2', refreshToken: 'refresh-2' }))
      .mockReturnValueOnce(json({ value: 7 }))
    const listener = vi.fn()
    const unsubscribe = subscribeRefreshedSession(listener)

    try {
      await expect(apiFetch<{ value: number }>('/api/protected')).resolves.toEqual({ value: 7 })
      expect(listener).toHaveBeenCalledWith({ accessToken: 'access-2', refreshToken: 'refresh-2', userId: 'admin' })
      expect(fetchMock).toHaveBeenCalledTimes(3)
    } finally {
      unsubscribe()
    }
  })

  it('refresh 실패 시 구독자에게 null을 전달해 App이 영속 세션을 지울 수 있게 함', async () => {
    fetchMock
      .mockResolvedValueOnce(new Response('', { status: 401 }))
      .mockResolvedValueOnce(new Response('', { status: 401 }))
    const listener = vi.fn()
    const unsubscribe = subscribeRefreshedSession(listener)

    try {
      await expect(apiFetch('/api/protected')).rejects.toMatchObject({ status: 401 })
      expect(listener).toHaveBeenCalledWith(null)
    } finally {
      unsubscribe()
    }
  })
})
