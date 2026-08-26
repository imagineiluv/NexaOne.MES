import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes } from 'react-router-dom'
import type { LoginResponse } from '../../api/auth'
import { ApiError } from '../../api/client'
import { DesignerHome } from '../DesignerHome'

const apiMocks = vi.hoisted(() => ({
  listDefinitions: vi.fn(),
  listScreenSeeds: vi.fn(),
  previewScreenSeed: vi.fn(),
  createDefinition: vi.fn(),
  importScreenSeed: vi.fn(),
}))

vi.mock('../../designer/api', async importOriginal => {
  const actual = await importOriginal<typeof import('../../designer/api')>()
  return { ...actual, ...apiMocks }
})

const session: LoginResponse = {
  accessToken: 'token', refreshToken: 'refresh', userId: 'admin', userName: '관리자',
  plantId: 'DEFAULT', roles: ['ADMIN'], requirePasswordChange: false,
}

describe('DesignerHome', () => {
  beforeEach(() => {
    apiMocks.listDefinitions.mockReset()
    apiMocks.listScreenSeeds.mockReset().mockResolvedValue([])
    apiMocks.previewScreenSeed.mockReset().mockRejectedValue(new ApiError(404, ''))
    apiMocks.createDefinition.mockReset()
    apiMocks.createDefinition.mockResolvedValue(1)
    apiMocks.importScreenSeed.mockReset().mockResolvedValue(undefined)
  })
  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('DB 화면 목록에서 대상 채널, 편집 링크, 런타임 경로를 표시', async () => {
    apiMocks.listDefinitions.mockResolvedValue([
      { uiId: 'POM_MES_WORK_EXECUTION', title: 'MES 작업 실행', targetChannel: 'MES', entryPath: '/meta/POM_MES_WORK_EXECUTION', updatedAt: null },
      { uiId: 'MOB_WORK', title: '모바일 작업', targetChannel: 'MOBILE', entryPath: '/Mobile/MOB_WORK', updatedAt: null },
      { uiId: 'POP_WORK', title: 'POP 작업', targetChannel: 'POP', entryPath: '/POP/POP_WORK', updatedAt: null },
    ])

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <DesignerHome session={session} onLogout={() => {}} />
      </MemoryRouter>,
    )

    expect(await screen.findByRole('heading', { name: '모바일 작업' })).toBeInTheDocument()
    expect(screen.getByText('MOBILE')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '모바일 작업 런타임 열기' })).toHaveAttribute('href', '/Mobile/MOB_WORK')
    expect(screen.getByRole('link', { name: '모바일 작업 편집' })).toHaveAttribute('href', '/Designer/MOB_WORK')
    expect(screen.getByRole('heading', { name: 'MES 작업 실행' })).toBeInTheDocument()
    expect(screen.getByText('MES')).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'MES 작업 실행 런타임 열기' })).toHaveAttribute(
      'href', '/meta/POM_MES_WORK_EXECUTION')
    expect(screen.getByRole('link', { name: 'MES 작업 실행 편집' })).toHaveAttribute(
      'href', '/Designer/POM_MES_WORK_EXECUTION')
    expect(screen.getAllByText('출처: DB')).toHaveLength(3)
  })

  it('MES 신규 화면은 기본 대상 채널로 저장한 뒤 해당 편집 경로로 이동', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <Routes>
          <Route path="/Designer" element={<DesignerHome session={session} onLogout={() => {}} />} />
          <Route path="/Designer/POM_MES_WORK_EXECUTION" element={<h1>MES 편집 경로 도착</h1>} />
        </Routes>
      </MemoryRouter>,
    )

    await screen.findByText('조건에 맞는 화면이 없습니다.')
    fireEvent.click(screen.getByRole('button', { name: '신규 화면' }))
    fireEvent.change(screen.getByLabelText('UI ID'), { target: { value: 'POM_MES_WORK_EXECUTION' } })
    fireEvent.change(screen.getByLabelText('화면 제목'), { target: { value: 'MES 작업 실행' } })
    expect(screen.getByLabelText('대상 채널')).toHaveValue('MES')
    fireEvent.click(screen.getByRole('button', { name: '생성 후 편집' }))

    await waitFor(() => expect(apiMocks.createDefinition).toHaveBeenCalledWith(
      'POM_MES_WORK_EXECUTION', 'MES 작업 실행', 'MES'))
    expect(await screen.findByRole('heading', { name: 'MES 편집 경로 도착' })).toBeInTheDocument()
  })

  it('신규 화면의 UI ID·제목·대상 채널을 저장한 뒤 편집 경로로 이동', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <Routes>
          <Route path="/Designer" element={<DesignerHome session={session} onLogout={() => {}} />} />
          <Route path="/Designer/:uiId" element={<h1>편집 경로 도착</h1>} />
        </Routes>
      </MemoryRouter>,
    )

    await screen.findByText('조건에 맞는 화면이 없습니다.')
    fireEvent.click(screen.getByRole('button', { name: '신규 화면' }))
    fireEvent.change(screen.getByLabelText('UI ID'), { target: { value: 'pom_mobile_work' } })
    fireEvent.change(screen.getByLabelText('화면 제목'), { target: { value: '공정 작업 실행' } })
    fireEvent.change(screen.getByLabelText('대상 채널'), { target: { value: 'MOBILE' } })
    fireEvent.click(screen.getByRole('button', { name: '생성 후 편집' }))

    await waitFor(() => expect(apiMocks.createDefinition).toHaveBeenCalledWith(
      'POM_MOBILE_WORK', '공정 작업 실행', 'MOBILE'))
    expect(await screen.findByRole('heading', { name: '편집 경로 도착' })).toBeInTheDocument()
  })

  it('코드 시드는 출처와 Auto 경고를 표시하고 확인 후 insert-only 가져오기 경로로 이동', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    apiMocks.listScreenSeeds.mockResolvedValue([{
      uiId: 'FACTORY_SLS_SALES_ORDER', title: '수주 관리', purpose: 'Auto',
      databaseExists: false, canImport: true, diagnostics: [], errorCount: 0, advisoryCount: 1,
      targetChannel: 'MES', entryPath: '/meta/FACTORY_SLS_SALES_ORDER',
    }])
    vi.spyOn(window, 'confirm').mockReturnValue(true)

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <Routes>
          <Route path="/Designer" element={<DesignerHome session={session} onLogout={() => {}} />} />
          <Route path="/Designer/FACTORY_SLS_SALES_ORDER" element={<h1>시드 편집 경로 도착</h1>} />
        </Routes>
      </MemoryRouter>,
    )

    expect(await screen.findByRole('heading', { name: '수주 관리' })).toBeInTheDocument()
    expect(screen.getByText('출처: 코드 시드')).toBeInTheDocument()
    expect(screen.getByText(/확인 필요: 화면 목적 Auto/)).toBeInTheDocument()
    expect(screen.getByRole('link', { name: '수주 관리 코드 시드 미리보기' }))
      .toHaveAttribute('href', '/Designer/FACTORY_SLS_SALES_ORDER')

    fireEvent.click(screen.getByRole('button', { name: '수주 관리 DB로 가져오기' }))

    await waitFor(() => expect(apiMocks.importScreenSeed).toHaveBeenCalledWith('FACTORY_SLS_SALES_ORDER'))
    expect(window.confirm).toHaveBeenCalledWith(expect.stringContaining('기존 DB 정의는 덮어쓰지 않습니다.'))
    expect(await screen.findByRole('heading', { name: '시드 편집 경로 도착' })).toBeInTheDocument()
  })

  it('capability 오류 시드는 이유를 텍스트로 표시하고 가져오기를 비활성화', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    apiMocks.listScreenSeeds.mockResolvedValue([{
      uiId: 'BAD_MANAGE', title: '잘못된 관리 화면', purpose: 'Manage',
      databaseExists: false, canImport: false,
      diagnostics: [{
        uiId: 'BAD_MANAGE', purpose: 'Manage', code: 'META-CAP-101', severity: 'Error', message: '입력 필요',
      }],
      errorCount: 1, advisoryCount: 0, targetChannel: 'MES', entryPath: '/meta/BAD_MANAGE',
    }])

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <DesignerHome session={session} onLogout={() => {}} />
      </MemoryRouter>,
    )

    expect(await screen.findByText('검증 오류 1건 · 가져오기 불가')).toHaveAttribute('role', 'alert')
    expect(screen.getByRole('button', { name: '잘못된 관리 화면 DB로 가져오기' })).toBeDisabled()
  })

  it('코드 시드와 같은 UI ID의 빈 신규 화면 생성을 막는다', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    apiMocks.listScreenSeeds.mockResolvedValue([{
      uiId: 'CODE_SCREEN', title: '코드 화면', purpose: 'Auto', databaseExists: false, canImport: true,
      diagnostics: [], errorCount: 0, advisoryCount: 1, targetChannel: 'MES', entryPath: '/meta/CODE_SCREEN',
    }])

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <DesignerHome session={session} onLogout={() => {}} />
      </MemoryRouter>,
    )

    await screen.findByRole('heading', { name: '코드 화면' })
    fireEvent.click(screen.getByRole('button', { name: '신규 화면' }))
    fireEvent.change(screen.getByLabelText('UI ID'), { target: { value: 'CODE_SCREEN' } })
    fireEvent.change(screen.getByLabelText('화면 제목'), { target: { value: '잘못된 빈 화면' } })
    fireEvent.click(screen.getByRole('button', { name: '생성 후 편집' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('코드 화면 가져오기를 사용하세요.')
    expect(apiMocks.createDefinition).not.toHaveBeenCalled()
  })

  it('대량 코드 시드는 처음 12개만 렌더링하고 검색 또는 더 보기로 나머지를 노출', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    apiMocks.listScreenSeeds.mockResolvedValue(Array.from({ length: 14 }, (_, index) => ({
      uiId: `SEED_${String(index + 1).padStart(2, '0')}`,
      title: `코드 화면 ${index + 1}`,
      purpose: 'Auto', databaseExists: false, canImport: true, diagnostics: [], errorCount: 0, advisoryCount: 1,
      targetChannel: 'MES', entryPath: `/meta/SEED_${String(index + 1).padStart(2, '0')}`,
    })))

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <DesignerHome session={session} onLogout={() => {}} />
      </MemoryRouter>,
    )

    await screen.findByRole('button', { name: '코드 화면 더 보기 (2개)' })
    expect(screen.getAllByRole('link', { name: /코드 시드 미리보기$/ })).toHaveLength(12)
    expect(screen.queryByRole('heading', { name: '코드 화면 14' })).not.toBeInTheDocument()

    fireEvent.change(screen.getByLabelText('화면 검색'), { target: { value: 'SEED_14' } })
    expect(await screen.findByRole('heading', { name: '코드 화면 14' })).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: /코드 시드 미리보기$/ })).toHaveLength(1)

    fireEvent.change(screen.getByLabelText('화면 검색'), { target: { value: '' } })
    fireEvent.click(await screen.findByRole('button', { name: '코드 화면 더 보기 (2개)' }))
    expect(await screen.findByRole('heading', { name: '코드 화면 14' })).toBeInTheDocument()
    expect(screen.getAllByRole('link', { name: /코드 시드 미리보기$/ })).toHaveLength(14)
  })

  it('목록에 없는 코드 시드 alias도 서버 canonical 조회로 찾아 빈 화면 생성을 차단', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    apiMocks.previewScreenSeed.mockResolvedValue({ uiId: 'FACTORY_SLS_SALES_ORDER', title: '수주 관리' })

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <DesignerHome session={session} onLogout={() => {}} />
      </MemoryRouter>,
    )

    await screen.findByText('조건에 맞는 화면이 없습니다.')
    fireEvent.click(screen.getByRole('button', { name: '신규 화면' }))
    fireEvent.change(screen.getByLabelText('UI ID'), { target: { value: 'SLS_ORDER_ALIAS' } })
    fireEvent.change(screen.getByLabelText('화면 제목'), { target: { value: '잘못된 빈 화면' } })
    fireEvent.click(screen.getByRole('button', { name: '생성 후 편집' }))

    expect(await screen.findByRole('alert')).toHaveTextContent("'FACTORY_SLS_SALES_ORDER' 화면을 DB로 가져오세요.")
    expect(apiMocks.previewScreenSeed).toHaveBeenCalledWith('SLS_ORDER_ALIAS')
    expect(apiMocks.createDefinition).not.toHaveBeenCalled()
  })

  it('코드 시드 확인이 404가 아닌 오류면 안전하게 신규 생성을 중단', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])
    apiMocks.previewScreenSeed.mockRejectedValue(new ApiError(503, 'unavailable'))

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <DesignerHome session={session} onLogout={() => {}} />
      </MemoryRouter>,
    )

    await screen.findByText('조건에 맞는 화면이 없습니다.')
    fireEvent.click(screen.getByRole('button', { name: '신규 화면' }))
    fireEvent.change(screen.getByLabelText('UI ID'), { target: { value: 'NEW_WHILE_OFFLINE' } })
    fireEvent.change(screen.getByLabelText('화면 제목'), { target: { value: '신규 화면' } })
    fireEvent.click(screen.getByRole('button', { name: '생성 후 편집' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('코드 시드 존재 여부를 확인하지 못해 생성을 중단했습니다.')
    expect(apiMocks.createDefinition).not.toHaveBeenCalled()
  })

  it('focuses the visible screen search shortcut with Ctrl+K', async () => {
    apiMocks.listDefinitions.mockResolvedValue([])

    render(
      <MemoryRouter future={{ v7_startTransition: true, v7_relativeSplatPath: true }} initialEntries={['/Designer']}>
        <DesignerHome session={session} onLogout={() => {}} />
      </MemoryRouter>,
    )

    const searchInput = await screen.findByRole('searchbox')
    expect(searchInput).not.toHaveFocus()

    fireEvent.keyDown(window, { key: 'k', ctrlKey: true })

    expect(searchInput).toHaveFocus()
  })
})
