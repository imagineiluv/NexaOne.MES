import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 개발 중 CORS 회피: /api·/hubs를 NexaOne.API로 프록시한다(ws:true는 SignalR 웹소켓).
// 운영은 VITE_API_BASE_URL을 직접 사용하고 API의 CORS AllowedOrigins에 SPA 오리진을 등록한다.
const apiProxy = process.env.VITE_API_PROXY ?? 'http://localhost:5181'

export default defineConfig({
  plugins: [react()],
  // Blazor 호스트(NexaOne.Web)가 /spa 경로로 임베드 서빙하므로 자산 기준 경로를 /spa/로 둔다(Phase 2 셸 임베드).
  base: '/spa/',
  build: {
    // dist를 통합 호스트(NexaOne.Server) wwwroot/spa에 출력 — `npm run build` 후 NexaOne.Server를 publish하면
    // /spa로 함께 정적 서빙된다(Phase 4: 정적 서빙 흡수). base는 '/spa/'로 유지.
    outDir: '../../00.Main/NexaOne.Server/wwwroot/spa',
    emptyOutDir: true,
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiProxy, changeOrigin: true },
      '/hubs': { target: apiProxy, changeOrigin: true, ws: true },
    },
  },
})
