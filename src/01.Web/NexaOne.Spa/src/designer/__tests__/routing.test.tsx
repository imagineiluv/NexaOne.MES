import { describe, it, expect } from 'vitest'
import { render, screen } from '@testing-library/react'
import { MemoryRouter } from 'react-router-dom'
import { AppRoutes } from '../../App'

describe('SPA 라우팅', () => {
  it('미인증 상태에서 /designer는 로그인으로 폴백', () => {
    render(
      <MemoryRouter initialEntries={['/designer/DEMO']}>
        <AppRoutes session={null} setSession={() => {}} />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: /Pro-Code/i })).toBeInTheDocument()
  })

  it('루트 경로는 로그인 화면', () => {
    render(
      <MemoryRouter initialEntries={['/']}>
        <AppRoutes session={null} setSession={() => {}} />
      </MemoryRouter>,
    )
    expect(screen.getByRole('heading', { name: /Pro-Code/i })).toBeInTheDocument()
  })
})
