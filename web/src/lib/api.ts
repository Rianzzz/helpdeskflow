import type { AuthResponse, User } from './types'

/**
 * Cliente HTTP da aplicação.
 *
 * SEGURANÇA DOS TOKENS
 *  • O access token (15 min) vive SÓ na memória do módulo: some ao fechar a aba e não fica em localStorage.
 *  • O refresh token fica no sessionStorage (por aba). É um compromisso consciente: ele continua legível por um
 *    script injetado (XSS), então a defesa principal é a CSP rígida do nginx e o React escapar todo conteúdo.
 *    O próximo passo de endurecimento é um cookie httpOnly emitido por um BFF.
 *
 * RENOVAÇÃO "SINGLE-FLIGHT"
 *  O backend ROTACIONA o refresh token e trata o reuso de um token antigo como roubo (derruba todas as sessões).
 *  Se três requisições recebessem 401 ao mesmo tempo e cada uma tentasse renovar, as duas últimas apresentariam um
 *  token já usado e o usuário seria deslogado à toa. Por isso todas compartilham UMA única renovação em andamento.
 */

const REFRESH_KEY = 'hdf.refresh'

export class ApiError extends Error {
  status: number
  title: string
  detail: string | null

  constructor(status: number, title: string, detail: string | null) {
    super(detail ?? title)
    this.name = 'ApiError'
    this.status = status
    this.title = title
    this.detail = detail
  }
}

let accessToken: string | null = null
let accessTokenExpiresAt = 0 // instante (ms) em que o access token vence
let refreshing: Promise<AuthResponse | null> | null = null
let onSessionChange: ((user: User | null) => void) | null = null

/** O AuthProvider registra aqui um ouvinte para saber quando a sessão é criada, renovada ou perdida. */
export function subscribeToSession(listener: (user: User | null) => void) {
  onSessionChange = listener
  return () => {
    if (onSessionChange === listener) onSessionChange = null
  }
}

export function storeSession(auth: AuthResponse) {
  accessToken = auth.accessToken
  accessTokenExpiresAt = Date.now() + auth.expiresInSeconds * 1000
  sessionStorage.setItem(REFRESH_KEY, auth.refreshToken)
  onSessionChange?.(auth.user)
}

export function clearSession() {
  accessToken = null
  accessTokenExpiresAt = 0
  sessionStorage.removeItem(REFRESH_KEY)
  onSessionChange?.(null)
}

export function hasStoredRefreshToken() {
  return sessionStorage.getItem(REFRESH_KEY) !== null
}

export function getAccessToken() {
  return accessToken
}

/**
 * Access token SEMPRE válido, para quem não pode esperar um 401 para renovar: a conexão em tempo real (SignalR) só
 * autentica na hora de conectar. Se faltar menos de 30 s para vencer, renova antes (pela mesma renovação single-flight).
 * Devolve "" se não há sessão (o servidor responde 401 e a conexão não se estabelece).
 */
export async function getValidAccessToken(): Promise<string> {
  if (accessToken && Date.now() < accessTokenExpiresAt - 30_000) return accessToken
  if (hasStoredRefreshToken()) await refreshSession()
  return accessToken ?? ''
}

/** Troca o refresh token por um novo par de tokens. Chamadas simultâneas compartilham a mesma promessa. */
export function refreshSession(): Promise<AuthResponse | null> {
  if (refreshing) return refreshing

  refreshing = (async () => {
    const refreshToken = sessionStorage.getItem(REFRESH_KEY)
    if (!refreshToken) return null

    try {
      const response = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })

      if (!response.ok) {
        // 401 = token expirado/revogado. Qualquer outra falha (rede, 5xx) NÃO deve deslogar o usuário.
        if (response.status === 401) clearSession()
        return null
      }

      const auth = (await response.json()) as AuthResponse
      storeSession(auth)
      return auth
    } catch {
      return null
    }
  })().finally(() => {
    refreshing = null
  })

  return refreshing
}

interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'DELETE'
  body?: unknown
  /** false nas rotas públicas (login, cadastro): não anexa token e não tenta renovar. */
  auth?: boolean
  signal?: AbortSignal
}

async function send(path: string, { method = 'GET', body, auth = true, signal }: RequestOptions) {
  const headers: Record<string, string> = {}
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (auth && accessToken) headers.Authorization = `Bearer ${accessToken}`

  return fetch(path, {
    method,
    headers,
    body: body === undefined ? undefined : JSON.stringify(body),
    signal,
  })
}

async function toError(response: Response): Promise<ApiError> {
  if (response.status === 429) {
    return new ApiError(429, 'Muitas requisições', 'Você fez muitas tentativas. Aguarde um instante e tente de novo.')
  }

  try {
    const problem = (await response.json()) as { title?: string; detail?: string | null }
    return new ApiError(response.status, problem.title ?? 'Erro', problem.detail ?? null)
  } catch {
    return new ApiError(response.status, response.status >= 500 ? 'Erro no servidor' : 'Erro', null)
  }
}

export async function api<T = void>(path: string, options: RequestOptions = {}): Promise<T> {
  const auth = options.auth ?? true
  let response = await send(path, options)

  // Token expirado: renova UMA vez (compartilhada) e repete a requisição original.
  if (response.status === 401 && auth && hasStoredRefreshToken()) {
    const renewed = await refreshSession()
    if (renewed) response = await send(path, options)
  }

  if (!response.ok) throw await toError(response)
  if (response.status === 204) return undefined as T
  return (await response.json()) as T
}

/** Útil nos testes: volta o módulo ao estado inicial. */
export function resetApiStateForTests() {
  accessToken = null
  accessTokenExpiresAt = 0
  refreshing = null
  onSessionChange = null
}
