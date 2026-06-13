import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'

// 개발 중 CORS 회피: /api·/hubs를 NexaOne.API로 프록시한다(ws:true는 SignalR 웹소켓).
// 운영은 VITE_API_BASE_URL을 직접 사용하고 API의 CORS AllowedOrigins에 SPA 오리진을 등록한다.
const apiProxy = process.env.VITE_API_PROXY ?? 'http://localhost:5080'

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      '/api': { target: apiProxy, changeOrigin: true },
      '/hubs': { target: apiProxy, changeOrigin: true, ws: true },
    },
  },
})
