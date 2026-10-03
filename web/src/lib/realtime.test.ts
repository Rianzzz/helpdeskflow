import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { resetApiStateForTests } from './api'
import type { AppNotification } from './types'

/** Conexão SignalR falsa: guarda os "ouvintes" para o teste poder simular o servidor empurrando mensagens e caindo. */
class FakeConnection {
  static instances: FakeConnection[] = []
  handlers = new Map<string, (...args: never[]) => void>()
  closeHandler?: () => void
  reconnectingHandler?: () => void
  reconnectedHandler?: () => void
  start = vi.fn<() => Promise<void>>(() => Promise.resolve())
  stop = vi.fn<() => Promise<void>>(() => Promise.resolve())

  constructor() {
    FakeConnection.instances.push(this)
  }
  on(name: string, handler: (...args: never[]) => void) {
    this.handlers.set(name, handler)
  }
  onclose(h: () => void) {
    this.closeHandler = h
  }
  onreconnecting(h: () => void) {
    this.reconnectingHandler = h
  }
  onreconnected(h: () => void) {
    this.reconnectedHandler = h
  }
  emit(name: string, ...args: unknown[]) {
    ;(this.handlers.get(name) as (...a: unknown[]) => void)(...args)
  }
}

let builderUrl = ''
let builderOptions: { accessTokenFactory?: () => string | Promise<string> } = {}
let reconnectPolicy: number[] | undefined

vi.mock('@microsoft/signalr', () => ({
  LogLevel: { Warning: 3 },
  HubConnectionBuilder: class {
    withUrl(url: string, options: typeof builderOptions) {
      builderUrl = url
      builderOptions = options
      return this
    }
    withAutomaticReconnect(policy: number[]) {
      reconnectPolicy = policy
      return this
    }
    configureLogging() {
      return this
    }
    build() {
      return new FakeConnection()
    }
  },
}))

const { getRealtimeStatus, startRealtime } = await import('./realtime')

const notification: AppNotification = { id: 'n1', subject: 'Novo chamado: X', body: 'b', createdAt: '2026-10-04T10:00:00Z', readAt: null }
const flush = () => vi.advanceTimersByTimeAsync(0)

const handlers = () => ({ onReceived: vi.fn(), onRead: vi.fn(), onResync: vi.fn() })
const current = () => FakeConnection.instances.at(-1)!

beforeEach(() => {
  vi.useFakeTimers()
  FakeConnection.instances = []
  resetApiStateForTests()
})

afterEach(() => vi.useRealTimers())

describe('startRealtime', () => {
  it('conecta no hub de notificações e fica "ao vivo", pedindo o recarregamento das listas', async () => {
    const h = handlers()

    const rt = startRealtime(h)
    expect(getRealtimeStatus()).toBe('connecting')
    await flush()

    expect(builderUrl).toBe('/hubs/notifications')
    expect(getRealtimeStatus()).toBe('connected')
    expect(h.onResync).toHaveBeenCalledTimes(1)
    rt.stop()
  })

  it('pede um token NOVO a cada conexão (nunca reutiliza um vencido) e se recusa a enviar token vazio como válido', async () => {
    const rt = startRealtime(handlers())
    await flush()

    expect(typeof builderOptions.accessTokenFactory).toBe('function')
    await expect(builderOptions.accessTokenFactory!()).resolves.toBe('') // sem sessão: o servidor responderá 401
    rt.stop()
  })

  it('usa uma política de reconexão automática com espera crescente', async () => {
    const rt = startRealtime(handlers())
    await flush()

    expect(reconnectPolicy).toEqual([0, 2_000, 5_000, 10_000, 30_000])
    rt.stop()
  })

  it('repassa o que o servidor empurra (aviso novo e aviso lido)', async () => {
    const h = handlers()
    const rt = startRealtime(h)
    await flush()

    current().emit('NotificationReceived', notification)
    current().emit('NotificationRead', 'n1')

    expect(h.onReceived).toHaveBeenCalledWith(notification)
    expect(h.onRead).toHaveBeenCalledWith('n1')
    rt.stop()
  })

  it('mostra "reconectando" na queda e recarrega as listas ao voltar (para pegar o que passou offline)', async () => {
    const h = handlers()
    const rt = startRealtime(h)
    await flush()
    h.onResync.mockClear()

    current().reconnectingHandler!()
    expect(getRealtimeStatus()).toBe('reconnecting')
    current().reconnectedHandler!()

    expect(getRealtimeStatus()).toBe('connected')
    expect(h.onResync).toHaveBeenCalledTimes(1)
    rt.stop()
  })

  it('quando o servidor encerra a conexão (token expirou), reinicia sozinha com espera crescente', async () => {
    const rt = startRealtime(handlers())
    await flush()
    const connection = current()

    connection.closeHandler!()
    expect(getRealtimeStatus()).toBe('offline')
    expect(connection.start).toHaveBeenCalledTimes(1)

    await vi.advanceTimersByTimeAsync(1_000) // primeira espera: 1 s
    expect(connection.start).toHaveBeenCalledTimes(2)
    expect(getRealtimeStatus()).toBe('connected')
    rt.stop()
  })

  it('se o servidor está fora, continua tentando com intervalos cada vez maiores', async () => {
    const rt = startRealtime(handlers())
    await flush()
    const connection = current()
    connection.start.mockRejectedValue(new Error('indisponível'))

    connection.closeHandler!()
    await vi.advanceTimersByTimeAsync(1_000) // tentativa 2 falha
    expect(connection.start).toHaveBeenCalledTimes(2)

    await vi.advanceTimersByTimeAsync(1_999) // ainda esperando (próxima espera: 2 s)
    expect(connection.start).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(1)
    expect(connection.start).toHaveBeenCalledTimes(3)
    expect(getRealtimeStatus()).toBe('offline')
    rt.stop()
  })

  it('stop() encerra a conexão e cancela qualquer reinício pendente', async () => {
    const rt = startRealtime(handlers())
    await flush()
    const connection = current()
    connection.closeHandler!() // deixa um reinício agendado

    rt.stop()
    await vi.advanceTimersByTimeAsync(60_000)

    expect(connection.stop).toHaveBeenCalled()
    expect(connection.start).toHaveBeenCalledTimes(1) // não reconectou depois do stop()
    expect(getRealtimeStatus()).toBe('offline')
  })

  it('parar enquanto ainda está conectando não deixa uma conexão órfã (caso do StrictMode do React)', async () => {
    const first = startRealtime(handlers())
    const connection = current()
    first.stop() // o React desmonta e remonta o efeito logo de cara
    await flush()

    expect(connection.stop).toHaveBeenCalled()
    expect(getRealtimeStatus()).toBe('offline')
  })
})
