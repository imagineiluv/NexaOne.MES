import { gzipSync } from 'node:zlib'
import { createLogger, defineConfig, loadEnv, type Plugin } from 'vite'
import react from '@vitejs/plugin-react'

const designerGzipBudgetBytes = 300 * 1024
const logger = createLogger()
const defaultWarn = logger.warn.bind(logger)
const defaultWarnOnce = logger.warnOnce.bind(logger)
const isIntegratedHostFontWarning = (message: string) =>
  message.includes('/fonts/PretendardVariable.woff2') && message.includes("didn't resolve at build time")

logger.warn = (message, options) => {
  // The integrated ASP.NET host owns this root asset. It is intentionally
  // outside the SPA build graph and is verified by the host asset tests.
  if (isIntegratedHostFontWarning(message)) return
  defaultWarn(message, options)
}

logger.warnOnce = (message, options) => {
  if (isIntegratedHostFontWarning(message)) return
  defaultWarnOnce(message, options)
}

function designerTransferBudget(): Plugin {
  return {
    name: 'nexa-designer-transfer-budget',
    generateBundle(_options, bundle) {
      for (const output of Object.values(bundle)) {
        if (output.type !== 'chunk' || output.name !== 'ScreenEditor') continue

        const gzipBytes = gzipSync(output.code).byteLength
        if (gzipBytes > designerGzipBudgetBytes) {
          this.error(
            `Designer route gzip size ${Math.ceil(gzipBytes / 1024)} KiB exceeds ` +
            `${designerGzipBudgetBytes / 1024} KiB budget.`,
          )
        }
      }
    },
  }
}

// 독립 Vite 개발 서버에서는 /api·/hubs를 통합 Server로 프록시한다(ws:true는 SignalR 웹소켓).
// 운영은 같은 Server의 /spa 경로에서 제공되므로 기본값은 same-origin이다.
export default defineConfig(({ command, mode }) => {
  const env = loadEnv(mode, process.cwd(), '')
  const apiProxy = env.VITE_API_PROXY || 'http://localhost:5173'

  return {
    plugins: [react(), designerTransferBudget()],
    customLogger: logger,
    // 개발 서버는 루트에서 각 채널 경로를 HMR하고, 배포 번들은 기존 /spa 자산 경로를 유지한다.
    // 사용자 단일 진입점은 통합 Server의 5173이며 Vite는 개발 보조 포트다.
    base: command === 'build' ? '/spa/' : '/',
    build: {
      // Portal은 Server/ClientApps 아래에 있고, 산출물만 Server/wwwroot/spa에 둔다.
      outDir: '../../wwwroot/spa',
      emptyOutDir: true,
      // GrapesJS is already isolated behind the lazy ScreenEditor route. Its
      // minified size is expected; the plugin above enforces transfer cost.
      chunkSizeWarningLimit: 1100,
    },
    server: {
      port: 5174,
      strictPort: true,
      proxy: {
        '/api': { target: apiProxy, changeOrigin: true },
        '/hubs': { target: apiProxy, changeOrigin: true, ws: true },
      },
    },
  }
})
