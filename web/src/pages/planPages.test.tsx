import { screen } from '@testing-library/react'
import { Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RequireAuth } from '../components/guards'
import { resetApiStateForTests } from '../lib/api'
import type { Role, User } from '../lib/types'
import { jsonResponse, makeAuth, makeUser, renderApp } from '../test/helpers'
import { CompanyPage } from './CompanyPage'
import { UsersPage } from './UsersPage'

const fetchMock = vi.fn<typeof fetch>()

const profile = (maxUsers = 5) => ({ id: 't-1', name: 'Acme', plan: 'Free', maxUsers, createdAt: '2026-10-01T10:00:00Z' })
const usersOf = (n: number): User[] => Array.from({ length: n }, (_, i) => makeUser(i === 0 ? 'Admin' : 'Agent', { id: `u-${i}`, name: `Pessoa ${i}`, email: `p${i}@acme.com` }))

/** Simula "já estou logado" com o papel desejado e responde ao que as telas pedem. */
function setup(role: Role, { users = 2, maxUsers = 5 }: { users?: number; maxUsers?: number } = {}) {
  sessionStorage.setItem('hdf.refresh', 'refresh-1')
  const calls: string[] = []
  fetchMock.mockImplementation(async (input, init) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`
    calls.push(key)
    if (key === 'POST /api/auth/refresh') return jsonResponse(makeAuth(role))
    if (key === 'GET /api/tenants/me') return jsonResponse(profile(maxUsers))
    if (key === 'GET /api/users') return jsonResponse(usersOf(users))
    throw new Error(`Requisição inesperada: ${key}`)
  })
  return calls
}

function renderPage(path: string, element: React.ReactElement) {
  return renderApp(
    <Routes>
      <Route element={<RequireAuth />}>
        <Route path={path} element={element} />
      </Route>
    </Routes>,
    path,
  )
}

beforeEach(() => {
  resetApiStateForTests()
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => vi.unstubAllGlobals())

describe('Usuários: limite do plano', () => {
  it('mostra o uso e deixa adicionar enquanto há vaga', async () => {
    setup('Admin', { users: 2, maxUsers: 5 })
    renderPage('/users', <UsersPage />)

    expect(await screen.findByText('2 de 5 usuários do plano Free.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Novo usuário' })).toBeEnabled()
    expect(screen.queryByText(/Limite do plano atingido/)).not.toBeInTheDocument()
  })

  it('com o plano lotado, bloqueia "Novo usuário" e explica o motivo', async () => {
    setup('Admin', { users: 5, maxUsers: 5 })
    renderPage('/users', <UsersPage />)

    expect(await screen.findByText('5 de 5 usuários do plano Free.')).toBeInTheDocument()
    const button = screen.getByRole('button', { name: 'Novo usuário' })
    expect(button).toBeDisabled()
    expect(button).toHaveAccessibleDescription(/Limite do plano atingido: 5 usuários/) // o leitor de tela explica o motivo
  })
})

describe('Empresa: uso do plano', () => {
  it('o administrador vê a barra de uso com os valores certos', async () => {
    setup('Admin', { users: 4, maxUsers: 5 })
    renderPage('/company', <CompanyPage />)

    const bar = await screen.findByRole('progressbar', { name: 'Usuários usados no plano' })
    expect(bar).toHaveAttribute('aria-valuenow', '4')
    expect(bar).toHaveAttribute('aria-valuemax', '5')
    expect(screen.getByText('4 de 5 usuários')).toBeInTheDocument()
    expect(screen.getByText(/ainda pode adicionar 1 pessoa\./)).toBeInTheDocument()
  })

  it('plano lotado mostra o convite ao upgrade', async () => {
    setup('Admin', { users: 5, maxUsers: 5 })
    renderPage('/company', <CompanyPage />)

    expect(await screen.findByText(/Plano lotado/)).toBeInTheDocument()
  })

  it('quem não é administrador vê o plano, mas não consulta nem exibe a lista de usuários', async () => {
    const calls = setup('Agent')
    renderPage('/company', <CompanyPage />)

    expect(await screen.findByText('Limite de usuários')).toBeInTheDocument()
    expect(screen.queryByRole('progressbar')).not.toBeInTheDocument()
    expect(calls).not.toContain('GET /api/users') // o servidor negaria (403); nem tentamos
  })
})
