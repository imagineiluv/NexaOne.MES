import { useEffect, useState } from 'react'
import { apiFetch, getAccessToken } from '../api/client'
import { logout, type LoginResponse } from '../api/auth'
import { hasPermission } from '../auth/jwt'
import { createHub } from '../realtime/hub'

// FDC 파라미터 그룹 DTO(계약 일부). 정식 타입은 `npm run gen:api`로 OpenAPI에서 생성.
interface FdcParameterGroup {
  id: string
  groupName: string
  equipmentId: string
}

export function Dashboard({ session, onLogout }: { session: LoginResponse; onLogout: () => void }) {
  const [groups, setGroups] = useState<FdcParameterGroup[]>([])
  const [loadError, setLoadError] = useState<string | null>(null)
  const [hubState, setHubState] = useState('연결 중…')
  const [lastEvent, setLastEvent] = useState('(없음)')

  useEffect(() => {
    // 인증된 REST 호출(권한 정책 PEP 적용 엔드포인트와 동일 토큰 사용)
    apiFetch<FdcParameterGroup[]>('/api/v1/fdc/parameter-groups?equipmentId=EQ-001')
      .then(setGroups)
      .catch(() => setLoadError('데이터 조회 실패(권한/엔드포인트/데이터 확인)'))

    // 실시간 채널(SignalR) — Event Bus 구독자(SignalR)가 푸시하는 도메인 이벤트 수신
    const hub = createHub()
    hub.on('EquipmentStateChanged', (p: unknown) => setLastEvent(`EquipmentStateChanged: ${JSON.stringify(p)}`))
    hub.on('AlarmUpdated', () => setLastEvent('AlarmUpdated'))
    hub.on('FdcDataReceived', (p: unknown) => setLastEvent(`FdcDataReceived: ${JSON.stringify(p)}`))
    hub.start().then(() => setHubState('연결됨')).catch(() => setHubState('연결 실패'))

    return () => { void hub.stop() }
  }, [])

  function handleLogout() {
    logout()
    onLogout()
  }

  // ADR-003 PEP와 동일 권한 모델로 UI 가드 — fdc:control 보유 시에만 제어 노출(서버도 동일 정책 강제)
  const canControl = hasPermission(getAccessToken(), 'fdc:control')

  async function startEquipment() {
    try {
      await apiFetch('/api/v1/fdc/equipment/start', { method: 'POST' })
      setLastEvent('설비 기동 요청 전송됨')
    } catch {
      setLastEvent('설비 기동 실패(권한/상태 확인)')
    }
  }

  return (
    <div style={{ maxWidth: 720, margin: '5vh auto', fontFamily: 'sans-serif' }}>
      <header style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <span>{session.userName} ({session.roles.join(', ')}) @ {session.plantId}</span>
        <button onClick={handleLogout}>로그아웃</button>
      </header>

      <h2>FDC 파라미터 그룹 (EQ-001)</h2>
      {loadError
        ? <p style={{ color: 'crimson' }}>{loadError}</p>
        : <ul>{groups.map(g => <li key={g.id}>{g.groupName}</li>)}</ul>}
      {!loadError && groups.length === 0 && <p>데이터가 없습니다.</p>}

      <h2>설비 제어 (권한 게이트)</h2>
      {canControl
        ? <button onClick={startEquipment}>설비 기동</button>
        : <p>제어 권한(fdc:control)이 없습니다.</p>}

      <h2>실시간(SignalR): {hubState}</h2>
      <p>최근 이벤트: {lastEvent}</p>
    </div>
  )
}
