import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { render } from '@testing-library/react'
import type { ReactElement } from 'react'
import { MemoryRouter } from 'react-router-dom'
import { ToastProvider } from '../components/ui'
import { AuthProvider } from '../lib/auth'
import type { AuthResponse, Role, Ticket, TicketComment, User } from '../lib/types'

export function makeUser(role: Role = 'Admin', overrides: Partial<User> = {}): User {
  return { id: 'u-1', tenantId: 't-1', name: 'Ana', email: 'ana@acme.com', role, isActive: true, ...overrides }
}

export function makeAuth(role: Role = 'Admin', overrides: Partial<AuthResponse> = {}): AuthResponse {
  return {
    accessToken: 'access-1',
    tokenType: 'Bearer',
    expiresInSeconds: 900,
    refreshToken: 'refresh-1',
    user: makeUser(role),
    ...overrides,
  }
}

export function makeTicket(overrides: Partial<Ticket> = {}): Ticket {
  return {
    id: 'k-1',
    requesterId: 'u-2',
    requesterName: 'Carla',
    title: 'Impressora',
    description: 'Sala 3',
    status: 'Open',
    priority: 'Medium',
    assigneeId: null,
    assigneeName: null,
    createdAt: '2026-10-01T10:00:00Z',
    closedAt: null,
    slaBreachedAt: null,
    ...overrides,
  }
}

export function makeComment(overrides: Partial<TicketComment> = {}): TicketComment {
  return {
    id: 'c-1',
    ticketId: 'k-1',
    authorId: 'u-9',
    authorName: 'Alex',
    authorRole: 'Agent',
    body: 'Estamos verificando.',
    isInternal: false,
    createdAt: '2026-10-01T11:00:00Z',
    ...overrides,
  }
}

export function jsonResponse(body: unknown, status = 200): Response {
  return new Response(status === 204 ? null : JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** Renderiza com TODOS os provedores reais (rotas, cache de dados, avisos e autenticação). */
export function renderApp(ui: ReactElement, route = '/') {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <MemoryRouter initialEntries={[route]}>
        <ToastProvider>
          <AuthProvider>{ui}</AuthProvider>
        </ToastProvider>
      </MemoryRouter>
    </QueryClientProvider>,
  )
}
