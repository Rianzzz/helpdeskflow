import type { Role, TicketPriority, TicketStatus } from './types'

export const statusLabel: Record<TicketStatus, string> = {
  Open: 'Aberto',
  InProgress: 'Em andamento',
  Resolved: 'Resolvido',
  Closed: 'Fechado',
}

export const priorityLabel: Record<TicketPriority, string> = {
  Low: 'Baixa',
  Medium: 'Média',
  High: 'Alta',
  Urgent: 'Urgente',
}

export const roleLabel: Record<Role, string> = {
  Admin: 'Administrador',
  Agent: 'Atendente',
  Customer: 'Cliente',
}

const relative = new Intl.RelativeTimeFormat('pt-BR', { numeric: 'auto' })
const dateTime = new Intl.DateTimeFormat('pt-BR', { dateStyle: 'short', timeStyle: 'short' })

/** "há 5 minutos", "ontem"... Aceita "agora" injetado para facilitar os testes. */
export function timeAgo(iso: string, now: Date = new Date()): string {
  const seconds = Math.round((new Date(iso).getTime() - now.getTime()) / 1000)
  const abs = Math.abs(seconds)

  if (abs < 45) return 'agora há pouco'
  if (abs < 3600) return relative.format(Math.round(seconds / 60), 'minute')
  if (abs < 86400) return relative.format(Math.round(seconds / 3600), 'hour')
  if (abs < 86400 * 30) return relative.format(Math.round(seconds / 86400), 'day')
  return formatDateTime(iso)
}

export function formatDateTime(iso: string): string {
  return dateTime.format(new Date(iso))
}

/** Mensagem amigável a partir de qualquer erro capturado. */
export function errorMessage(error: unknown, fallback = 'Algo deu errado. Tente novamente.'): string {
  if (error instanceof Error && error.message) return error.message
  return fallback
}
