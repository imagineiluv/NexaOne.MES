import { type HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { getAccessToken } from '../api/client'

// /hubs/smartees — JWT는 access_token 쿼리스트링으로 전달(NexaOneEESHub.OnMessageReceived 처리).
const HUB_BASE = import.meta.env.VITE_API_BASE_URL ?? ''

export function createHub(): HubConnection {
  return new HubConnectionBuilder()
    .withUrl(`${HUB_BASE}/hubs/smartees`, { accessTokenFactory: () => getAccessToken() ?? '' })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build()
}
