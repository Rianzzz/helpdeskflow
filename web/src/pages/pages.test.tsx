import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PublicOnly, RequireAuth, RequireRole } from '../components/guards'
import { resetApiStateForTests } from '../lib/api'
import type { Role, Ticket, TicketComment } from '../lib/types'
import { jsonResponse, makeAuth, makeTicket, renderApp } from '../test/helpers'
import { LoginPage } from './LoginPage'
import { RegisterPage, RegistrationStatusPage } from './RegisterPage'
import { TicketDetailPage } from './TicketDetailPage'

type Handler = (body: unknown) => Response | Promise<Response>
type FakeServer = { ticket: Ticket; comments?: TicketComment[] }
const fetchMock = vi.fn<typeof fetch>()

/** Roteia o fetch falso por "MÉTODO /caminho". Rotas não previstas falham o teste em vez de passar em silêncio. */
function mockApi(routes: Record<string, Handler>) {
  fetchMock.mockImplementation(async (input, init) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`
    const handler = routes[key]
    if (!handler) throw new Error(`Requisição inesperada: ${key}`)
    return handler(init?.body ? JSON.parse(String(init.body)) : undefined)
  })
}

/** Simula "já estou logado" (refresh token guardado na aba) com o papel desejado. */
function loggedInAs(role: Role): Record<string, Handler> {
  sessionStorage.setItem('hdf.refresh', 'refresh-1')
  return { 'POST /api/auth/refresh': () => jsonResponse(makeAuth(role)) }
}

beforeEach(() => {
  resetApiStateForTests()
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => vi.unstubAllGlobals())

describe('LoginPage', () => {
  function renderLogin() {
    return renderApp(
      <Routes>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/tickets" element={<p>Tela de chamados</p>} />
      </Routes>,
      '/login',
    )
  }

  it('entra e vai para os chamados quando as credenciais estão certas', async () => {
    mockApi({ 'POST /api/auth/login': () => jsonResponse(makeAuth()) })
    const user = userEvent.setup()
    renderLogin()

    await user.type(screen.getByLabelText('E-mail'), 'ana@acme.com')
    await user.type(screen.getByLabelText('Senha'), 'senhaForte123')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(await screen.findByText('Tela de chamados')).toBeInTheDocument()
  })

  it('mostra uma mensagem genérica no 401 (sem dizer se foi o e-mail ou a senha)', async () => {
    mockApi({ 'POST /api/auth/login': () => jsonResponse({ title: 'Não autorizado', detail: 'Credenciais inválidas.' }, 401) })
    const user = userEvent.setup()
    renderLogin()

    await user.type(screen.getByLabelText('E-mail'), 'ana@acme.com')
    await user.type(screen.getByLabelText('Senha'), 'errada12345')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent('E-mail ou senha incorretos')
    expect(screen.queryByText('Tela de chamados')).not.toBeInTheDocument()
  })

  it('mantém o botão desabilitado enquanto faltam campos', () => {
    renderLogin()

    expect(screen.getByRole('button', { name: 'Entrar' })).toBeDisabled()
  })
})

describe('guardas de rota', () => {
  function renderGuarded(route: string) {
    return renderApp(
      <Routes>
        <Route path="/login" element={<p>Tela de login</p>} />
        <Route element={<RequireAuth />}>
          <Route path="/tickets" element={<p>Chamados</p>} />
          <Route element={<RequireRole roles={['Admin']} />}>
            <Route path="/users" element={<p>Lista de usuários</p>} />
          </Route>
        </Route>
      </Routes>,
      route,
    )
  }

  it('visitante é mandado para o login', async () => {
    renderGuarded('/tickets')

    expect(await screen.findByText('Tela de login')).toBeInTheDocument()
  })

  it('depois de entrar, volta para a página que a pessoa tentou abrir (e não sempre para os chamados)', async () => {
    mockApi({ 'POST /api/auth/login': () => jsonResponse(makeAuth()) })
    const user = userEvent.setup()
    renderApp(
      <Routes>
        <Route element={<PublicOnly />}>
          <Route path="/login" element={<LoginPage />} />
        </Route>
        <Route element={<RequireAuth />}>
          <Route path="/tickets" element={<p>Chamados</p>} />
          <Route path="/notifications" element={<p>Tela de notificações</p>} />
        </Route>
      </Routes>,
      '/notifications',
    )

    await user.type(await screen.findByLabelText('E-mail'), 'ana@acme.com')
    await user.type(screen.getByLabelText('Senha'), 'senhaForte123')
    await user.click(screen.getByRole('button', { name: 'Entrar' }))

    expect(await screen.findByText('Tela de notificações')).toBeInTheDocument()
  })

  it('admin acessa a área de usuários', async () => {
    mockApi(loggedInAs('Admin'))
    renderGuarded('/users')

    expect(await screen.findByText('Lista de usuários')).toBeInTheDocument()
  })

  it('quem não é admin vê "Acesso restrito" na área de usuários', async () => {
    mockApi(loggedInAs('Customer'))
    renderGuarded('/users')

    expect(await screen.findByText('Acesso restrito')).toBeInTheDocument()
    expect(screen.queryByText('Lista de usuários')).not.toBeInTheDocument()
  })

  it('sessão que não consegue ser restaurada volta para o login', async () => {
    sessionStorage.setItem('hdf.refresh', 'expirado')
    mockApi({ 'POST /api/auth/refresh': () => jsonResponse({}, 401) })
    renderGuarded('/tickets')

    expect(await screen.findByText('Tela de login')).toBeInTheDocument()
  })
})

describe('RegisterPage', () => {
  it('recusa senha fraca no navegador e NÃO chama o servidor', async () => {
    const user = userEvent.setup()
    renderApp(<RegisterPage />, '/register')

    await user.type(screen.getByLabelText('Nome da empresa'), 'Acme')
    await user.type(screen.getByLabelText('Seu nome'), 'Ana')
    await user.type(screen.getByLabelText('E-mail'), 'ana@acme.com')
    await user.type(screen.getByLabelText('Senha'), 'curta1')
    await user.click(screen.getByRole('button', { name: 'Criar empresa' }))

    expect(await screen.findByText('Use pelo menos 10 caracteres.')).toBeInTheDocument()
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('envia o cadastro e acompanha o status até a empresa ficar ativa', async () => {
    let polls = 0
    mockApi({
      'POST /api/auth/register-tenant': (body) => {
        expect(body).toMatchObject({ companyName: 'Acme', adminName: 'Ana', email: 'ana@acme.com' })
        return jsonResponse({ tenantId: 'tn-1', status: 'Provisioning' }, 202)
      },
      'GET /api/auth/tenants/tn-1/status': () =>
        jsonResponse({ tenantId: 'tn-1', status: ++polls < 2 ? 'Provisioning' : 'Active', failureReason: null }),
    })
    const user = userEvent.setup()
    renderApp(
      <Routes>
        <Route path="/register" element={<RegisterPage />} />
        <Route path="/register/status/:tenantId" element={<RegistrationStatusPage />} />
      </Routes>,
      '/register',
    )

    await user.type(screen.getByLabelText('Nome da empresa'), 'Acme')
    await user.type(screen.getByLabelText('Seu nome'), 'Ana')
    await user.type(screen.getByLabelText('E-mail'), 'ana@acme.com')
    await user.type(screen.getByLabelText('Senha'), 'senhaForte123')
    await user.click(screen.getByRole('button', { name: 'Criar empresa' }))

    expect(await screen.findByText('Criando a sua empresa…')).toBeInTheDocument()
    expect(await screen.findByText('Empresa pronta!', undefined, { timeout: 4000 })).toBeInTheDocument()
  })

  it('mostra o motivo quando o cadastro é recusado e deixa tentar de novo', async () => {
    mockApi({
      'GET /api/auth/tenants/tn-2/status': () =>
        jsonResponse({ tenantId: 'tn-2', status: 'Failed', failureReason: 'Já existe uma empresa com este nome.' }),
    })
    renderApp(
      <Routes>
        <Route path="/register/status/:tenantId" element={<RegistrationStatusPage />} />
        <Route path="/register" element={<RegisterPage />} />
      </Routes>,
      '/register/status/tn-2',
    )

    expect(await screen.findByText('Já existe uma empresa com este nome.')).toBeInTheDocument()
    await userEvent.setup().click(screen.getByRole('button', { name: 'Tentar novamente' }))
    expect(await screen.findByText(/O cadastro anterior não foi concluído/)).toBeInTheDocument()
  })
})

describe('TicketDetailPage: ações por papel', () => {
  /** O "servidor" falso é stateful: depois de resolver, o GET passa a devolver o chamado resolvido (como o real). */
  function renderDetail(role: Role, server: FakeServer = { ticket: makeTicket() }, extra: Record<string, Handler> = {}) {
    mockApi({
      ...loggedInAs(role),
      'GET /api/tickets/k-1': () => jsonResponse(server.ticket),
      'GET /api/tickets/k-1/comments': () => jsonResponse(server.comments ?? []),
      'GET /api/tickets/staff': () => jsonResponse([{ id: 'u-1', name: 'Ana', role: 'Admin' }]),
      ...extra,
    })
    return renderApp(
      <Routes>
        <Route element={<RequireAuth />}>
          <Route path="/tickets/:id" element={<TicketDetailPage />} />
        </Route>
      </Routes>,
      '/tickets/k-1',
    )
  }

  it('cliente não vê atribuir, resolver nem fechar', async () => {
    renderDetail('Customer')

    expect(await screen.findByText('Impressora')).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Marcar como resolvido' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Assumir chamado' })).not.toBeInTheDocument()
  })

  it('equipe vê as ações e resolver chama a API', async () => {
    const server = { ticket: makeTicket() }
    const resolve = vi.fn(() => {
      server.ticket = makeTicket({ status: 'Resolved' })
      return jsonResponse(server.ticket)
    })
    renderDetail('Agent', server, { 'PUT /api/tickets/k-1/resolve': resolve })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Marcar como resolvido' }))

    await waitFor(() => expect(resolve).toHaveBeenCalled())
    expect(await screen.findByText('Resolvido')).toBeInTheDocument()
  })

  it('mostra o aviso de SLA estourado', async () => {
    renderDetail('Admin', { ticket: makeTicket({ slaBreachedAt: '2026-10-01T12:00:00Z' }) })

    expect(await screen.findByText(/prazo de primeiro atendimento estourou/)).toBeInTheDocument()
  })

  it('exibe a descrição como TEXTO: HTML digitado por um usuário não vira marcação', async () => {
    renderDetail('Admin', { ticket: makeTicket({ description: '<img src=x onerror=alert(1)> olá' }) })

    expect(await screen.findByText('<img src=x onerror=alert(1)> olá')).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })

  it('traduz o erro de negócio do servidor em aviso para o usuário', async () => {
    renderDetail('Agent', undefined, {
      'PUT /api/tickets/k-1/resolve': () =>
        jsonResponse({ title: 'Regra de negócio violada', status: 400, detail: 'Chamado fechado não pode ser alterado.' }, 400),
    })
    const user = userEvent.setup()

    await user.click(await screen.findByRole('button', { name: 'Marcar como resolvido' }))

    expect(await screen.findByText('Chamado fechado não pode ser alterado.')).toBeInTheDocument()
  })
})
