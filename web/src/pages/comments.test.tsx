import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { Route, Routes } from 'react-router-dom'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { RequireAuth } from '../components/guards'
import { resetApiStateForTests } from '../lib/api'
import type { Role, Ticket, TicketComment } from '../lib/types'
import { jsonResponse, makeAuth, makeComment, makeTicket, renderApp } from '../test/helpers'
import { TicketDetailPage } from './TicketDetailPage'

const fetchMock = vi.fn<typeof fetch>()

interface Setup {
  role: Role
  ticket?: Ticket
  comments?: TicketComment[]
  /** Chamado por POST /comments; devolve a resposta falsa do servidor. */
  onPost?: (body: { body: string; isInternal: boolean }) => Response
}

function setup({ role, ticket = makeTicket(), comments = [], onPost }: Setup) {
  sessionStorage.setItem('hdf.refresh', 'refresh-1')
  const posts: { body: string; isInternal: boolean }[] = []
  let list = comments

  fetchMock.mockImplementation(async (input, init) => {
    const key = `${init?.method ?? 'GET'} ${String(input)}`
    if (key === 'POST /api/auth/refresh') return jsonResponse(makeAuth(role))
    if (key === 'GET /api/tickets/k-1') return jsonResponse(ticket)
    if (key === 'GET /api/tickets/staff') return jsonResponse([])
    if (key === 'GET /api/tickets/k-1/comments') return jsonResponse(list)
    if (key === 'POST /api/tickets/k-1/comments') {
      const body = JSON.parse(String(init?.body)) as { body: string; isInternal: boolean }
      posts.push(body)
      if (onPost) return onPost(body)
      const created = makeComment({ id: `c-${posts.length}`, authorId: 'u-1', authorName: 'Ana', body: body.body, isInternal: body.isInternal })
      list = [...list, created]
      return jsonResponse(created, 201)
    }
    throw new Error(`Requisição inesperada: ${key}`)
  })

  renderApp(
    <Routes>
      <Route element={<RequireAuth />}>
        <Route path="/tickets/:id" element={<TicketDetailPage />} />
      </Route>
    </Routes>,
    '/tickets/k-1',
  )
  return { posts }
}

beforeEach(() => {
  resetApiStateForTests()
  fetchMock.mockReset()
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => vi.unstubAllGlobals())

describe('Conversa do chamado', () => {
  it('mostra as mensagens com autor, papel e "Você" para as próprias', async () => {
    setup({
      role: 'Customer',
      comments: [
        makeComment({ id: 'a', authorId: 'u-9', authorName: 'Alex', authorRole: 'Agent', body: 'Pode reiniciar?' }),
        makeComment({ id: 'b', authorId: 'u-1', authorName: 'Ana', authorRole: 'Customer', body: 'Já reiniciei.' }),
      ],
    })

    const thread = await screen.findByRole('list', { name: 'Mensagens do chamado' })
    const [first, second] = within(thread).getAllByRole('listitem')
    expect(within(first).getByText('Alex')).toBeInTheDocument()
    expect(within(first).getByText('Atendente')).toBeInTheDocument()
    expect(within(first).getByText('Pode reiniciar?')).toBeInTheDocument()
    expect(within(second).getByText('Você')).toBeInTheDocument() // u-1 é a pessoa logada
  })

  it('mostra uma mensagem amigável quando ainda não há conversa', async () => {
    setup({ role: 'Agent' })

    expect(await screen.findByText(/Ainda não há mensagens/)).toBeInTheDocument()
  })

  it('autor ainda desconhecido (replicação) aparece como "Usuário", sem quebrar', async () => {
    setup({ role: 'Agent', comments: [makeComment({ authorName: null, authorRole: null })] })

    expect(await screen.findByText('Usuário')).toBeInTheDocument()
  })

  it('exibe o texto como TEXTO: HTML digitado em um comentário nunca vira marcação', async () => {
    setup({ role: 'Agent', comments: [makeComment({ body: '<img src=x onerror=alert(1)> oi' })] })

    expect(await screen.findByText('<img src=x onerror=alert(1)> oi')).toBeInTheDocument()
    expect(document.querySelector('img')).toBeNull()
  })

  it('destaca a nota interna, que a equipe recebe do servidor', async () => {
    setup({
      role: 'Agent',
      comments: [makeComment({ id: 'a', body: 'pública' }), makeComment({ id: 'b', body: 'só equipe', isInternal: true })],
    })

    const items = within(await screen.findByRole('list', { name: 'Mensagens do chamado' })).getAllByRole('listitem')
    expect(items[0]).toHaveAttribute('data-internal', 'false')
    expect(items[1]).toHaveAttribute('data-internal', 'true')
    expect(within(items[1]).getByText('Nota interna')).toBeInTheDocument()
  })
})

describe('Compositor', () => {
  it('cliente não vê a opção de nota interna', async () => {
    setup({ role: 'Customer' })

    expect(await screen.findByLabelText('Sua mensagem')).toBeInTheDocument()
    expect(screen.queryByLabelText(/Nota interna/)).not.toBeInTheDocument()
  })

  it('equipe vê a opção e, ao marcá-la, o formulário muda de modo e de rótulo', async () => {
    setup({ role: 'Agent' })
    const user = userEvent.setup()

    await user.click(await screen.findByLabelText('Nota interna (só a equipe vê)'))

    expect(screen.getByLabelText('Nota interna (invisível para o cliente)')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Adicionar nota interna' })).toBeInTheDocument()
  })

  it('não deixa enviar mensagem vazia ou só com espaços', async () => {
    setup({ role: 'Agent' })
    const user = userEvent.setup()
    const send = await screen.findByRole('button', { name: 'Enviar' })

    expect(send).toBeDisabled()
    await user.type(screen.getByLabelText('Sua mensagem'), '   ')
    expect(send).toBeDisabled()
  })

  it('envia o texto sem espaços sobrando, limpa o campo e a mensagem aparece na conversa', async () => {
    const { posts } = setup({ role: 'Customer' })
    const user = userEvent.setup()

    await user.type(await screen.findByLabelText('Sua mensagem'), '  Obrigado!  ')
    await user.click(screen.getByRole('button', { name: 'Enviar' }))

    await waitFor(() => expect(posts).toEqual([{ body: 'Obrigado!', isInternal: false }]))
    expect(await screen.findByText('Mensagem enviada.')).toBeInTheDocument()
    expect(screen.getByLabelText('Sua mensagem')).toHaveValue('')
    expect(await within(screen.getByRole('list', { name: 'Mensagens do chamado' })).findByText('Obrigado!')).toBeInTheDocument()
  })

  it('a equipe envia nota interna com isInternal verdadeiro', async () => {
    const { posts } = setup({ role: 'Agent' })
    const user = userEvent.setup()

    await user.click(await screen.findByLabelText('Nota interna (só a equipe vê)'))
    await user.type(screen.getByLabelText('Nota interna (invisível para o cliente)'), 'Cuidado com esse cliente')
    await user.click(screen.getByRole('button', { name: 'Adicionar nota interna' }))

    await waitFor(() => expect(posts).toEqual([{ body: 'Cuidado com esse cliente', isInternal: true }]))
    expect(await screen.findByText('Nota interna adicionada.')).toBeInTheDocument()
  })

  it('Ctrl+Enter envia, como em qualquer ferramenta de conversa', async () => {
    const { posts } = setup({ role: 'Customer' })
    const user = userEvent.setup()

    await user.type(await screen.findByLabelText('Sua mensagem'), 'Atalho funciona{Control>}{Enter}{/Control}')

    await waitFor(() => expect(posts).toHaveLength(1))
    expect(posts[0].body).toBe('Atalho funciona')
  })

  it('mostra o motivo quando o servidor recusa (ex.: regra de negócio)', async () => {
    setup({
      role: 'Agent',
      onPost: () => jsonResponse({ title: 'Regra de negócio violada', status: 400, detail: 'O comentário não pode ser vazio.' }, 400),
    })
    const user = userEvent.setup()

    await user.type(await screen.findByLabelText('Sua mensagem'), 'texto')
    await user.click(screen.getByRole('button', { name: 'Enviar' }))

    expect(await screen.findByText('O comentário não pode ser vazio.')).toBeInTheDocument()
    expect(screen.getByLabelText('Sua mensagem')).toHaveValue('texto') // não perde o que a pessoa digitou
  })

  it('chamado fechado troca o formulário por um aviso (é preciso reabrir antes)', async () => {
    setup({ role: 'Customer', ticket: makeTicket({ status: 'Closed', closedAt: '2026-10-02T10:00:00Z' }) })

    expect(await screen.findByText(/Este chamado está fechado/)).toBeInTheDocument()
    expect(screen.queryByLabelText('Sua mensagem')).not.toBeInTheDocument()
  })
})
