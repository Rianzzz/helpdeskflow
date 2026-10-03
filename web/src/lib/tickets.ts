import type { Ticket, TicketPriority, TicketStatus } from './types'

/** Urgentes primeiro, depois alta, média, baixa; dentro de cada prioridade, o mais antigo antes. */
const priorityRank: Record<TicketPriority, number> = { Urgent: 0, High: 1, Medium: 2, Low: 3 }

export function filterTickets(tickets: Ticket[], status: TicketStatus | 'all', search: string): Ticket[] {
  const term = search.trim().toLowerCase()
  return tickets
    .filter((t) => status === 'all' || t.status === status)
    .filter((t) => !term || t.title.toLowerCase().includes(term) || (t.requesterName ?? '').toLowerCase().includes(term))
    .sort((a, b) => priorityRank[a.priority] - priorityRank[b.priority] || a.createdAt.localeCompare(b.createdAt))
}

/**
 * Quais ações a tela OFERECE para cada situação. É só conveniência de uso: o servidor valida de novo o papel
 * (403) e a regra de negócio (400) em toda chamada.
 */
export function availableActions(ticket: Ticket, isStaff: boolean) {
  const open = ticket.status === 'Open' || ticket.status === 'InProgress'
  return {
    assign: isStaff && open,
    resolve: isStaff && open,
    close: isStaff && ticket.status === 'Resolved',
    reopen: ticket.status === 'Resolved' || ticket.status === 'Closed',
  }
}

