import { fileURLToPath } from 'node:url'
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'

const repositoryRoot = fileURLToPath(new URL('../../../../../', import.meta.url))

// 디자이너 코어(매핑/직렬화/API)는 브라우저 비의존 순수 로직 → jsdom로 충분.
// GrapesJS 캔버스 자체는 단위 테스트 비대상(수동/플레이wright). 설정 빌더는 순수라 테스트한다.
export default defineConfig({
  plugins: [react()],
  server: {
    // Shared design tokens live at the repository root. Vitest 4 applies Vite's
    // filesystem boundary during module loading, so permit that exact tree.
    fs: { allow: [repositoryRoot] },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
  },
})
