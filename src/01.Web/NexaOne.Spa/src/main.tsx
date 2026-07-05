import React from 'react'
import ReactDOM from 'react-dom/client'
import { BrowserRouter } from 'react-router-dom'
import { App } from './App'
import { ErrorBoundary } from './components/ErrorBoundary'
import './styles/nexaone.css'

// P3-17 다크 모드 — 호스트 셸과 같은 localStorage 키('nx-theme')를 읽어 첫 페인트 전에 적용한다
// (호스트에서 다크로 전환 후 /spa 디자이너로 진입해도 테마가 이어진다).
document.documentElement.dataset.theme = localStorage.getItem('nx-theme') || 'light'

ReactDOM.createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <ErrorBoundary>
      <BrowserRouter basename="/spa">
        <App />
      </BrowserRouter>
    </ErrorBoundary>
  </React.StrictMode>,
)
