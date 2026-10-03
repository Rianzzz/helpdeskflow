import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { jsonResponse, makeAuth } from '../test/helpers'
import { ApiError, api, clearSession, getAccessToken, hasStoredRefreshToken, resetApiStateForTests, storeSession, subscribeToSession } from './api'

const fetchMock = vi.fn<typeof fetch>()

beforeEach(() => {
  resetApiStateForTests()
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => vi.unstubAllGlobals())

function authorizationOf(call: unknown[]): string | undefined {
  const headers = (call[1] as RequestInit).headers as Record<string, string> | undefined
  return headers?.Authorization
}

describe('sessão e tokens', () => {
  it('guarda o access token só na memória e o refresh token no sessionStorage', () => {
    storeSession(makeAuth())

    expect(getAccessToken()).toBe('access-1')
    expect(sessionStorage.getItem('hdf.refresh')).toBe('refresh-1')
    expect(localStorage.length).toBe(0) // nunca em localStorage
  })

  it('avisa o ouvinte quando a sessão muda e quando é encerrada', () => {
    const listener = vi.fn()
    subscribeToSession(listener)

    storeSession(makeAuth('Agent'))
    clearSession()

    expect(listener).toHaveBeenNthCalledWith(1, expect.objectContaining({ role: 'Agent' }))
    expect(listener).toHaveBeenNthCalledWith(2, null)
    expect(hasStoredRefreshToken()).toBe(false)
  })
})

describe('api()', () => {
  it('anexa o Bearer token quando logado', async () => {
    storeSession(makeAuth())
    fetchMock.mockResolvedValueOnce(jsonResponse([]))

    await api('/api/tickets')

    expect(authorizationOf(fetchMock.mock.calls[0])).toBe('Bearer access-1')
  })

  it('não anexa token nas rotas públicas', async () => {
    storeSession(makeAuth())
    fetchMock.mockResolvedValueOnce(jsonResponse({ ok: true }))

    await api('/api/auth/login', { method: 'POST', body: { a: 1 }, auth: false })

    expect(authorizationOf(fetchMock.mock.calls[0])).toBeUndefined()
  })

  it('devolve undefined em respostas 204', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse(null, 204))

    await expect(api('/api/notifications/1/read', { method: 'PUT' })).resolves.toBeUndefined()
  })

  it('transforma o erro do servidor em ApiError com título e detalhe', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({ title: 'Regra de negócio violada', status: 400, detail: 'O título é obrigatório.' }, 400))

    const error = await api('/api/tickets').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 400, title: 'Regra de negócio violada', message: 'O título é obrigatório.' })
  })

  it('mostra uma mensagem amigável no limite de requisições (429)', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 429))

    await expect(api('/api/auth/login', { auth: false })).rejects.toMatchObject({
      status: 429,
      message: expect.stringContaining('muitas tentativas'),
    })
  })

  it('não vaza resposta que não é JSON (ex.: página de erro do proxy)', async () => {
    fetchMock.mockResolvedValueOnce(new Response('<html>502</html>', { status: 502 }))

    await expect(api('/api/tickets')).rejects.toMatchObject({ status: 502, title: 'Erro no servidor', detail: null })
  })
})

describe('renovação de token', () => {
  it('renova UMA vez ao receber 401 e repete a requisição com o token novo', async () => {
    storeSession(makeAuth())
    fetchMock
      .mockResolvedValueOnce(jsonResponse({}, 401)) // token expirado
      .mockResolvedValueOnce(jsonResponse(makeAuth('Admin', { accessToken: 'access-2', refreshToken: 'refresh-2' }))) // refresh
      .mockResolvedValueOnce(jsonResponse([{ id: 1 }])) // repetição

    const result = await api('/api/tickets')

    expect(result).toEqual([{ id: 1 }])
    expect(fetchMock.mock.calls.map((c) => c[0])).toEqual(['/api/tickets', '/api/auth/refresh', '/api/tickets'])
    expect(authorizationOf(fetchMock.mock.calls[2])).toBe('Bearer access-2')
    expect(sessionStorage.getItem('hdf.refresh')).toBe('refresh-2') // o refresh token ROTACIONOU
  })

  it('várias requisições com 401 ao mesmo tempo compartilham UMA renovação (senão o reuso derrubaria a sessão)', async () => {
    storeSession(makeAuth())
    let refreshCalls = 0
    fetchMock.mockImplementation(async (input, init) => {
      const url = String(input)
      if (url === '/api/auth/refresh') {
        refreshCalls++
        await new Promise((r) => setTimeout(r, 20)) // dá tempo de as outras requisições também falharem
        return jsonResponse(makeAuth('Admin', { accessToken: 'access-2', refreshToken: 'refresh-2' }))
      }
      const token = (init?.headers as Record<string, string> | undefined)?.Authorization
      return token === 'Bearer access-2' ? jsonResponse({ url }) : jsonResponse({}, 401)
    })

    const results = await Promise.all([api('/api/tickets'), api('/api/notifications'), api('/api/users')])

    expect(results).toEqual([{ url: '/api/tickets' }, { url: '/api/notifications' }, { url: '/api/users' }])
    expect(refreshCalls).toBe(1)
  })

  it('se o refresh token foi rejeitado (401), encerra a sessão e devolve o erro original', async () => {
    storeSession(makeAuth())
    const listener = vi.fn()
    subscribeToSession(listener)
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 401)).mockResolvedValueOnce(jsonResponse({}, 401))

    await expect(api('/api/tickets')).rejects.toMatchObject({ status: 401 })

    expect(getAccessToken()).toBeNull()
    expect(hasStoredRefreshToken()).toBe(false)
    expect(listener).toHaveBeenLastCalledWith(null)
  })

  it('falha de rede durante a renovação NÃO desloga o usuário', async () => {
    storeSession(makeAuth())
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 401)).mockRejectedValueOnce(new TypeError('Failed to fetch'))

    await expect(api('/api/tickets')).rejects.toMatchObject({ status: 401 })

    expect(hasStoredRefreshToken()).toBe(true) // continua com o refresh token para tentar de novo depois
  })

  it('não tenta renovar quando não há refresh token', async () => {
    fetchMock.mockResolvedValueOnce(jsonResponse({}, 401))

    await expect(api('/api/tickets')).rejects.toMatchObject({ status: 401 })

    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
