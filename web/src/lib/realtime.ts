import { HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useSyncExternalStore } from 'react'
import { getValidAccessToken } from './api'
import type { AppNotification } from './types'

/**
 * Conexão em tempo real com o serviço de notificações (SignalR). O servidor EMPURRA cada notificação assim que ela
 * nasce; o navegador não precisa ficar perguntando. O SignalR escolhe sozinho o melhor transporte (WebSocket, com
 * fallback para Server-Sent Events e long polling quando algum proxy bloqueia).
 *
 * É um ACELERADOR, não a fonte da verdade: a lista continua vindo da API REST. Por isso, ao (re)conectar, as listas são
 * recarregadas (para pegar o que passou enquanto estávamos offline), e enquanto não houver conexão a tela volta a
 * consultar a API periodicamente.
 */

export type RealtimeStatus = 'connecting' | 'connected' | 'reconnecting' | 'offline'

let status: RealtimeStatus = 'offline'
const listeners = new Set<() => void>()

function setStatus(next: RealtimeStatus) {
  if (next === status) return
  status = next
  listeners.forEach((l) => l())
}

export const getRealtimeStatus = () => status

function subscribe(listener: () => void) {
  listeners.add(listener)
  return () => {
    listeners.delete(listener)
  }
}

/** Estado da conexão para a interface (indicador "Ao vivo") e para decidir se ainda é preciso consultar a API. */
export function useRealtimeStatus(): RealtimeStatus {
  return useSyncExternalStore(subscribe, getRealtimeStatus, getRealtimeStatus)
}

export interface RealtimeHandlers {
  onReceived: (notification: AppNotification) => void
  onRead: (id: string) => void
  /** Chamado a cada (re)conexão: hora de recarregar o que pode ter sido perdido enquanto estávamos desconectados. */
  onResync: () => void
}

const RESTART_DELAYS_MS = [1_000, 2_000, 5_000, 10_000, 30_000]

export function startRealtime(handlers: RealtimeHandlers): { stop: () => void } {
  let stopped = false
  let attempt = 0
  let restartTimer: ReturnType<typeof setTimeout> | undefined

  const connection = new HubConnectionBuilder()
    // A cada (re)conexão o SignalR pede um token NOVO: nunca reutiliza um já vencido.
    .withUrl('/hubs/notifications', { accessTokenFactory: () => getValidAccessToken() })
    // Quedas inesperadas (rede, deploy): tenta de novo logo e depois com intervalos maiores.
    .withAutomaticReconnect([0, 2_000, 5_000, 10_000, 30_000])
    .configureLogging(LogLevel.Warning)
    .build()

  connection.on('NotificationReceived', handlers.onReceived)
  connection.on('NotificationRead', handlers.onRead)

  connection.onreconnecting(() => setStatus('reconnecting'))
  connection.onreconnected(() => {
    attempt = 0
    setStatus('connected')
    handlers.onResync()
  })

  // O servidor ENCERRA a conexão quando o token expira (CloseOnAuthenticationExpiration). Isso não dispara a
  // reconexão automática, então reiniciamos manualmente (já com um token novo) enquanto a pessoa estiver logada.
  connection.onclose(() => {
    setStatus('offline')
    scheduleRestart()
  })

  function scheduleRestart() {
    if (stopped) return
    const delay = RESTART_DELAYS_MS[Math.min(attempt, RESTART_DELAYS_MS.length - 1)]
    attempt++
    restartTimer = setTimeout(() => void connect(), delay)
  }

  async function connect() {
    if (stopped) return
    setStatus('connecting')
    try {
      await connection.start()
      if (stopped) return void connection.stop()
      attempt = 0
      setStatus('connected')
      handlers.onResync()
    } catch {
      // Servidor fora, sessão encerrada, proxy bloqueando: continua tentando com espera crescente.
      if (!stopped) {
        setStatus('offline')
        scheduleRestart()
      }
    }
  }

  void connect()

  return {
    stop() {
      stopped = true
      clearTimeout(restartTimer)
      setStatus('offline')
      void connection.stop()
    },
  }
}
