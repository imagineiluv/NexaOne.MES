import { describe, it, expect } from 'vitest'
import { render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, useLocation } from 'react-router-dom'
import { AppRoutes } from '../../App'
import type { LoginResponse } from '../../api/auth'

function PathProbe() {
  return <output aria-label="현재 경로">{useLocation().pathname}</output>
}

const unauthorizedSession: LoginResponse = {
  accessToken: 'not-a-jwt', refreshToken: 'refresh', userId: 'user', userName: '사용자',
  plantId: 'DEFAULT', roles: [], requirePasswordChange: false,
}

describe('SPA 라우팅', () => {
  it('미인증 상태에서 /Designer/:uiId는 목적 경로를 유지한 디자이너 로그인', () => {
    render(
      <MemoryRouter initialEntries={['/Designer/DEMO']}>
        <AppRoutes session={null} setSession={() => {}} />
        <PathProbe />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: /NexaOne 디자이너/i })).toBeInTheDocument()
    expect(screen.getByLabelText('현재 경로')).toHaveTextContent('/Designer/DEMO')
  })

  it('루트 경로는 Dashboard 대신 /Designer로 수렴', async () => {
    render(
      <MemoryRouter initialEntries={['/']}>
        <AppRoutes session={null} setSession={() => {}} />
        <PathProbe />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: /NexaOne 디자이너/i })).toBeInTheDocument()
    await waitFor(() => expect(screen.getByLabelText('현재 경로')).toHaveTextContent('/Designer'))
  })

  it('/spa/designer/:uiId 호환 경로는 정식 /Designer/:uiId로 리다이렉트', async () => {
    render(
      <MemoryRouter initialEntries={['/spa/designer/LEGACY']}>
        <AppRoutes session={null} setSession={() => {}} />
        <PathProbe />
      </MemoryRouter>,
    )
    await waitFor(() => expect(screen.getByLabelText('현재 경로')).toHaveTextContent('/Designer/LEGACY'))
    expect(screen.getByRole('heading', { name: /NexaOne 디자이너/i })).toBeInTheDocument()
  })

  it('인증됐지만 sys:manage가 없으면 디자이너 접근을 거부', () => {
    render(
      <MemoryRouter initialEntries={['/Designer']}>
        <AppRoutes session={unauthorizedSession} setSession={() => {}} />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: '디자이너 접근 권한이 없습니다.' })).toBeInTheDocument()
  })
})
