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
    // dist를 Blazor wwwroot/spa에 출력 — `npm run build` 후 NexaOne.Web을 publish하면 /spa로 함께 배포된다.
    outDir: '../NexaOne.Web/wwwroot/spa',
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
