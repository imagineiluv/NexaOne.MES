import React from 'react'

// 렌더 단계 예외를 잡아 화이트스크린 대신 친절한 폴백을 보여준다(전체 SPA 다운 방지).
// 클래스 컴포넌트인 이유: getDerivedStateFromError/componentDidCatch는 함수형 훅으로 대체 불가.
interface ErrorBoundaryProps {
  children: React.ReactNode
}

interface ErrorBoundaryState {
  hasError: boolean
}

export class ErrorBoundary extends React.Component<ErrorBoundaryProps, ErrorBoundaryState> {
  constructor(props: ErrorBoundaryProps) {
    super(props)
    this.state = { hasError: false }
  }

  static getDerivedStateFromError(): ErrorBoundaryState {
    return { hasError: true }
  }

  componentDidCatch(error: Error, info: React.ErrorInfo): void {
    // 진단용으로만 남긴다 — 외부 텔레메트리 연동은 별도 작업.
    console.error('SPA render error:', error, info.componentStack)
  }

  render(): React.ReactNode {
    if (this.state.hasError) {
      return (
        <div role="alert" style={{ padding: '2rem', fontFamily: 'sans-serif' }}>
          <h1>문제가 발생했습니다</h1>
          <p>화면을 표시하는 중 오류가 발생했습니다. 페이지를 새로고침해 주세요.</p>
          <a href={window.location.href}>새로고침</a>
        </div>
      )
    }
    return this.props.children
  }
}
