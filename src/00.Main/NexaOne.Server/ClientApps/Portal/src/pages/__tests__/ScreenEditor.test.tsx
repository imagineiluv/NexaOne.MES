import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { MemoryRouter, Route, Routes, useLocation } from 'react-router-dom'
import { SCREEN_PURPOSE_VALUES, type LayoutNode, type ScreenDefinitionDto } from '../../designer/layout'
import { ScreenEditor } from '../ScreenEditor'

const mocks = vi.hoisted(() => ({
  loadDefinition: vi.fn(),
  importScreenSeed: vi.fn(),
  saveDefinition: vi.fn(),
  listQueries: vi.fn(),
  readRootLayout: vi.fn(),
  initEditor: vi.fn(),
}))

vi.mock('../../api/client', () => ({ getAccessToken: () => 'token' }))
vi.mock('../../auth/jwt', () => ({ hasPermission: () => true }))
vi.mock('../../designer/api', async importOriginal => {
  const actual = await importOriginal<typeof import('../../designer/api')>()
  return {
    ...actual,
    loadDefinition: mocks.loadDefinition,
    importScreenSeed: mocks.importScreenSeed,
    saveDefinition: mocks.saveDefinition,
    listQueries: mocks.listQueries,
  }
})
vi.mock('../../designer/editorBridge', () => ({ readRootLayout: mocks.readRootLayout }))
vi.mock('grapesjs', () => ({ default: { init: mocks.initEditor } }))

const layout: LayoutNode = { kind: 'section', id: 'root', children: [] }

function definition(purpose: ScreenDefinitionDto['purpose']): ScreenDefinitionDto {
  return {
    uiId: 'QMS_REGISTER', title: '검사 등록', purpose,
    fields: [], columns: null, queryId: null, saveQueryId: null, layout,
  }
}

function createEditorStub() {
  const canvasDocument = document.implementation.createHTMLDocument()
  const addedComponent = { is: vi.fn(() => false) }
  const rootAppend = vi.fn(() => [addedComponent])
  const rootComponent = {
    is: vi.fn((type: string) => type === 'nx-section'),
    parent: vi.fn(() => undefined),
    append: rootAppend,
  }
  const wrapper = {
    components: vi.fn(() => ({ length: 1 })),
    getChildAt: vi.fn(() => rootComponent),
  }
  return {
    DomComponents: { addType: vi.fn() },
    BlockManager: { add: vi.fn() },
    Canvas: { getDocument: vi.fn(() => canvasDocument) },
    on: vi.fn(),
    setComponents: vi.fn(),
    destroy: vi.fn(),
    getSelected: vi.fn(() => rootComponent),
    getWrapper: vi.fn(() => wrapper),
    select: vi.fn(),
    rootAppend,
    canvasDocument,
  }
}

function LocationProbe() {
  return <output data-testid="designer-path">{useLocation().pathname}</output>
}

function renderEditor(initialUiId = 'QMS_REGISTER') {
  return render(
    <MemoryRouter initialEntries={[`/Designer/${initialUiId}`]}>
      <Routes>
        <Route path="/Designer/:uiId" element={<><ScreenEditor /><LocationProbe /></>} />
      </Routes>
    </MemoryRouter>,
  )
}

describe('ScreenEditor 화면 목적 속성', () => {
  beforeEach(() => {
    mocks.loadDefinition.mockReset()
    mocks.importScreenSeed.mockReset().mockResolvedValue(undefined)
    mocks.saveDefinition.mockReset().mockResolvedValue(1)
    mocks.listQueries.mockReset().mockResolvedValue({ reads: [], writes: [] })
    mocks.readRootLayout.mockReset().mockReturnValue(layout)
    mocks.initEditor.mockReset().mockReturnValue(createEditorStub())
  })
  afterEach(() => {
    cleanup()
    vi.restoreAllMocks()
  })

  it('공유 허용값을 모두 표시하고 기존 Auto 정의를 하위 호환한다', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Auto'),
      targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER',
      source: 'database', diagnostics: [], canImport: false,
    })

    renderEditor()

    const purpose = await screen.findByLabelText('화면 목적')
    await waitFor(() => expect(purpose).toBeEnabled())
    expect(purpose).toHaveValue('Auto')
    expect(Array.from((purpose as HTMLSelectElement).options, option => option.value))
      .toEqual([...SCREEN_PURPOSE_VALUES])
  })

  it('선택한 화면 목적을 저장 payload에 반영한다', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Manage'),
      targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER',
      source: 'database', diagnostics: [], canImport: false,
    })

    renderEditor()

    const purpose = await screen.findByLabelText('화면 목적')
    await waitFor(() => expect(purpose).toBeEnabled())
    expect(purpose).toHaveValue('Manage')
    fireEvent.change(purpose, { target: { value: 'Register' } })
    fireEvent.click(screen.getByRole('button', { name: '저장' }))

    await waitFor(() => expect(mocks.saveDefinition).toHaveBeenCalledTimes(1))
    expect(mocks.saveDefinition.mock.calls[0][3]).toMatchObject({
      purpose: 'Register',
      targetChannel: 'MES',
      entryPath: '/meta/QMS_REGISTER',
    })
  })

  it('Manage 화면에서만 4가지 보기 모드를 저장과 분리해 캔버스에 미리보기한다', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Manage'),
      targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER',
      source: 'database', diagnostics: [], canImport: false,
    })
    const editor = createEditorStub()
    mocks.initEditor.mockReturnValue(editor)

    renderEditor()

    const selector = await screen.findByLabelText('보기 모드 미리보기')
    await waitFor(() => expect(selector).toBeEnabled())
    expect(Array.from((selector as HTMLSelectElement).options, option => option.value))
      .toEqual(['standard', 'dense', 'cards', 'split'])
    expect(selector).toHaveValue('standard')
    expect(editor.canvasDocument.documentElement.dataset.nxManagePreview).toBe('standard')
    expect(screen.getByText('미리보기 전용 · 저장되지 않음')).toBeInTheDocument()
    expect(screen.getByText(/개인 설정은 MES 화면에서 사용자별로 저장됩니다/)).toBeInTheDocument()

    fireEvent.change(selector, { target: { value: 'cards' } })

    await waitFor(() => expect(editor.canvasDocument.documentElement.dataset.nxManagePreview).toBe('cards'))
    expect(screen.getByText('레코드를 구분된 카드 목록으로 표현한 구성을 확인합니다.')).toBeInTheDocument()
    expect(screen.getByText('DB 정의 · 편집 가능')).toBeInTheDocument()
    expect(mocks.saveDefinition).not.toHaveBeenCalled()

    fireEvent.click(screen.getByRole('button', { name: '저장' }))
    await waitFor(() => expect(mocks.saveDefinition).toHaveBeenCalledTimes(1))
    expect(mocks.saveDefinition.mock.calls[0][3]).not.toHaveProperty('managePreviewMode')

    fireEvent.change(screen.getByLabelText('화면 목적'), { target: { value: 'Inquiry' } })
    expect(screen.queryByLabelText('보기 모드 미리보기')).not.toBeInTheDocument()
    await waitFor(() => expect(editor.canvasDocument.documentElement.dataset.nxManagePreview).toBe('standard'))
  })

  it('블록 팔레트는 키보드 버튼 계약을 제공하고 Enter·Space로 선택 영역에 추가한다', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Register'),
      targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER',
      source: 'database', diagnostics: [], canImport: false,
    })
    const editor = createEditorStub()
    mocks.initEditor.mockImplementationOnce((config: {
      blockManager: { appendTo?: HTMLElement }
    }) => {
      editor.BlockManager.add.mockImplementation((id: string, options: {
        label: string
        attributes: Record<string, string>
        onClick?: (block: unknown, activeEditor: typeof editor) => void
      }) => {
        const element = document.createElement('div')
        element.innerHTML = options.label
        Object.entries(options.attributes).forEach(([name, value]) => element.setAttribute(name, value))
        element.addEventListener('click', () => options.onClick?.({ id }, editor))
        config.blockManager.appendTo?.appendChild(element)
      })
      return editor
    })

    renderEditor()

    await waitFor(() => expect(screen.getByLabelText('화면 목적')).toBeEnabled())
    const rowBlock = screen.getByRole('button', { name: /행 블록.*Enter 키로 추가/ })
    expect(rowBlock).toHaveAttribute('tabindex', '0')
    expect(rowBlock).toHaveAttribute('data-nx-block', 'nx-row')

    fireEvent.keyDown(rowBlock, { key: 'Enter' })
    fireEvent.keyDown(rowBlock, { key: ' ' })

    expect(editor.rootAppend).toHaveBeenCalledTimes(2)
    expect(editor.select).toHaveBeenCalledTimes(2)
    expect(screen.getByText('행 추가됨 — 저장하면 DB에 반영됩니다.')).toBeInTheDocument()
  })

  it('Ctrl+S 단축키는 편집 가능한 DB 화면을 저장한다', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Register'),
      targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER',
      source: 'database', diagnostics: [], canImport: false,
    })

    renderEditor()
    await waitFor(() => expect(screen.getByRole('button', { name: '저장' })).toBeEnabled())
    fireEvent.keyDown(window, { key: 's', ctrlKey: true })

    await waitFor(() => expect(mocks.saveDefinition).toHaveBeenCalledTimes(1))
    expect(screen.getByText('저장됨')).toBeInTheDocument()
  })

  it('컬렉션 구조 오류는 권한 오류로 숨기지 않고 저장 원인을 안내한다', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Register'),
      targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER',
      source: 'database', diagnostics: [], canImport: false,
    })
    mocks.saveDefinition.mockRejectedValue(
      new Error('화면 레이아웃 구조가 올바르지 않습니다: layout.collectionKey가 비어 있습니다.'),
    )

    renderEditor()

    const save = await screen.findByRole('button', { name: '저장' })
    await waitFor(() => expect(save).toBeEnabled())
    fireEvent.click(save)

    expect(await screen.findByRole('alert')).toHaveTextContent('collectionKey가 비어 있습니다.')
    expect(screen.getByText('저장 실패(레이아웃 구조 확인)')).toBeInTheDocument()
  })

  it('코드 시드는 원본 JSON을 미리 보되 모든 편집을 잠그고 가져온 뒤에만 편집을 허용', async () => {
    const seed = {
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Auto'),
      targetChannel: 'MES' as const, entryPath: '/meta/QMS_REGISTER', source: 'seed' as const,
      diagnostics: [{
        uiId: 'QMS_REGISTER', purpose: 'Auto' as const, code: 'META-CAP-000', severity: 'Advisory' as const,
        message: 'Purpose가 Auto입니다.',
      }],
      canImport: true,
    }
    const database = { ...seed, source: 'database' as const, diagnostics: [], canImport: false }
    mocks.loadDefinition.mockResolvedValueOnce(seed).mockResolvedValueOnce(database)
    vi.spyOn(window, 'confirm').mockReturnValue(true)

    renderEditor()

    expect(await screen.findByText('읽기 전용 코드 시드 미리보기')).toBeInTheDocument()
    expect(screen.getByText('출처: 코드 시드')).toBeInTheDocument()
    expect(screen.getByLabelText('화면 제목')).toBeDisabled()
    expect(screen.getByLabelText('화면 목적')).toBeDisabled()
    expect(screen.getByLabelText('대상 채널')).toBeDisabled()
    expect(screen.getByRole('button', { name: '저장' })).toBeDisabled()

    fireEvent.click(screen.getByRole('button', { name: 'DB로 가져와 편집' }))

    await waitFor(() => expect(mocks.importScreenSeed).toHaveBeenCalledWith('QMS_REGISTER'))
    await waitFor(() => expect(screen.getByText('출처: DB')).toBeInTheDocument())
    expect(mocks.loadDefinition).toHaveBeenCalledTimes(2)
    expect(screen.getByLabelText('화면 제목')).toBeEnabled()
    expect(screen.getByLabelText('화면 목적')).toBeEnabled()
    expect(screen.getByRole('button', { name: '저장' })).toBeEnabled()
  })

  it('capability 오류가 있는 코드 시드는 진단을 경고하고 가져오기를 비활성화', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Manage'),
      targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER', source: 'seed', canImport: false,
      diagnostics: [{
        uiId: 'QMS_REGISTER', purpose: 'Manage', code: 'META-CAP-101', severity: 'Error', message: '입력 필요',
      }],
    })

    renderEditor()

    expect(await screen.findByText('가져오기 전 검증 오류')).toBeInTheDocument()
    expect(screen.getByRole('alert', { name: '코드 시드 상태' })).toHaveTextContent('META-CAP-101: 입력 필요')
    expect(screen.getByRole('button', { name: 'DB로 가져와 편집' })).toBeDisabled()
  })

  it('DB와 코드 시드가 없는 직접 경로는 빈 정의 저장을 차단', async () => {
    mocks.loadDefinition.mockResolvedValue({
      canonicalUiId: 'QMS_REGISTER',
      title: '', layout: null, flat: null, targetChannel: 'MES', entryPath: '/meta/QMS_REGISTER',
      source: 'missing', diagnostics: [], canImport: false,
    })

    renderEditor()

    expect(await screen.findByRole('alert')).toHaveTextContent('화면 정의를 찾을 수 없습니다.')
    expect(screen.getByRole('button', { name: '저장' })).toBeDisabled()
    expect(screen.queryByRole('button', { name: 'DB로 가져와 편집' })).not.toBeInTheDocument()
  })

  it('코드 시드 alias 경로는 canonical UI ID 경로로 replace 이동한 뒤 다시 로드', async () => {
    const canonicalSeed = {
      canonicalUiId: 'QMS_REGISTER',
      title: '검사 등록', layout, flat: definition('Auto'),
      targetChannel: 'MES' as const, entryPath: '/meta/QMS_REGISTER', source: 'seed' as const,
      diagnostics: [], canImport: true,
    }
    mocks.loadDefinition
      .mockResolvedValueOnce(canonicalSeed)
      .mockResolvedValueOnce(canonicalSeed)

    renderEditor('QMS_REGISTER_ALIAS')

    await waitFor(() => expect(mocks.loadDefinition.mock.calls.map(call => call[0]))
      .toEqual(['QMS_REGISTER_ALIAS', 'QMS_REGISTER']))
    expect(screen.getByTestId('designer-path')).toHaveTextContent('/Designer/QMS_REGISTER')
    expect(await screen.findByText('출처: 코드 시드')).toBeInTheDocument()
  })
})
