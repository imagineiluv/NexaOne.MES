import { type HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { getAccessToken } from '../api/client'

// 기본 /hubs/smartees. 다른 host가 RealtimeHubPath를 바꾸면 같은 경로를 build env로 전달한다.
// JWT는 access_token 쿼리스트링으로 전달(NexaOneEESHub.OnMessageReceived 처리).
const HUB_BASE = import.meta.env.VITE_API_BASE_URL ?? ''
const HUB_PATH = import.meta.env.VITE_REALTIME_HUB_PATH ?? '/hubs/smartees'

export function createHub(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${HUB_BASE}${HUB_PATH}`, { accessTokenFactory: () => getAccessToken() ?? '' })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
}
