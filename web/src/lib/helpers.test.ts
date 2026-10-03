import { describe, expect, it } from 'vitest'
import { makeTicket } from '../test/helpers'
import { errorMessage, timeAgo } from './format'
import { availableActions, filterTickets } from './tickets'
import { passwordProblem } from './validation'

const now = new Date('2026-10-03T12:00:00Z')

describe('timeAgo', () => {
  it('diz "agora há pouco" para menos de um minuto', () => {
    expect(timeAgo('2026-10-03T11:59:30Z', now)).toBe('agora há pouco')
  })

  it('usa minutos, horas e dias em português', () => {
    expect(timeAgo('2026-10-03T11:55:00Z', now)).toBe('há 5 minutos')
    expect(timeAgo('2026-10-03T09:00:00Z', now)).toBe('há 3 horas')
    expect(timeAgo('2026-10-02T12:00:00Z', now)).toBe('ontem')
  })

  it('para datas antigas mostra a data completa', () => {
    expect(timeAgo('2026-08-01T12:00:00Z', now)).toMatch(/\d{2}\/\d{2}\/\d{4}/)
  })
})

describe('errorMessage', () => {
  it('usa a mensagem do erro e tem um texto padrão', () => {
    expect(errorMessage(new Error('falhou'))).toBe('falhou')
    expect(errorMessage('qualquer coisa')).toBe('Algo deu errado. Tente novamente.')
  })
})

describe('passwordProblem (espelha a política do servidor)', () => {
  it.each([
    ['curta1', 'pelo menos 10'],
    ['somenteletrasaqui', 'letras e números'],
    ['12345678901234', 'letras e números'],
    ['a'.repeat(120) + '1234567890', 'no máximo 128'],
  ])('rejeita %s', (password, expected) => {
    expect(passwordProblem(password)).toContain(expected)
  })

  it('aceita uma senha razoável, inclusive com acentos', () => {
    expect(passwordProblem('senhaForte123')).toBeNull()
    expect(passwordProblem('çomplexa2026ok')).toBeNull()
  })
})

describe('filterTickets', () => {
  const tickets = [
    makeTicket({ id: 'a', title: 'Wi-Fi caiu', priority: 'Low', createdAt: '2026-10-01T10:00:00Z' }),
    makeTicket({ id: 'b', title: 'Servidor fora', priority: 'Urgent', createdAt: '2026-10-02T10:00:00Z' }),
    makeTicket({ id: 'c', title: 'Impressora', priority: 'Urgent', createdAt: '2026-10-01T09:00:00Z', status: 'Resolved', requesterName: 'Beatriz' }),
  ]

  it('ordena por prioridade e, dentro dela, o mais antigo primeiro', () => {
    expect(filterTickets(tickets, 'all', '').map((t) => t.id)).toEqual(['c', 'b', 'a'])
  })

  it('filtra por status', () => {
    expect(filterTickets(tickets, 'Resolved', '').map((t) => t.id)).toEqual(['c'])
  })

  it('busca no título e no nome do solicitante, sem diferenciar maiúsculas', () => {
    expect(filterTickets(tickets, 'all', 'servidor').map((t) => t.id)).toEqual(['b'])
    expect(filterTickets(tickets, 'all', 'BEATRIZ').map((t) => t.id)).toEqual(['c'])
  })
})

describe('availableActions', () => {
  it('equipe pode assumir e resolver chamados em aberto', () => {
    expect(availableActions(makeTicket({ status: 'Open' }), true)).toEqual({ assign: true, resolve: true, close: false, reopen: false })
  })

  it('só se fecha o que já foi resolvido', () => {
    expect(availableActions(makeTicket({ status: 'Resolved' }), true)).toEqual({ assign: false, resolve: false, close: true, reopen: true })
  })

  it('cliente nunca vê atribuir, resolver ou fechar, mas pode reabrir', () => {
    expect(availableActions(makeTicket({ status: 'Open' }), false)).toEqual({ assign: false, resolve: false, close: false, reopen: false })
    expect(availableActions(makeTicket({ status: 'Closed' }), false)).toMatchObject({ resolve: false, close: false, reopen: true })
  })
})
