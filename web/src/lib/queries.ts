import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import { useRealtimeStatus, type RealtimeStatus } from './realtime'
import type { AppNotification, NewComment, NewTicket, NewUser, StaffMember, TenantProfile, Ticket, TicketComment, User } from './types'

// Chaves de cache centralizadas: quem altera dados invalida a chave certa.
export const keys = {
  tickets: ['tickets'] as const,
  ticket: (id: string) => ['tickets', id] as const,
  comments: (id: string) => ['tickets', id, 'comments'] as const,
  staff: ['staff'] as const,
  notifications: ['notifications'] as const,
  unread: ['notifications', 'unread'] as const,
  users: ['users'] as const,
  tenant: ['tenant'] as const,
}

/**
 * A lista se atualiza sozinha a cada 15 s (pausa em aba escondida) e também na hora em que chega uma notificação em
 * tempo real. A consulta periódica fica porque nem toda mudança num chamado gera notificação (ex.: fechar, reabrir).
 */
export const useTickets = () =>
  useQuery({ queryKey: keys.tickets, queryFn: () => api<Ticket[]>('/api/tickets'), refetchInterval: 15_000 })

export const useTicket = (id: string) =>
  useQuery({ queryKey: keys.ticket(id), queryFn: () => api<Ticket>(`/api/tickets/${id}`) })

/**
 * Conversa do chamado. A chave começa por ['tickets', id], então qualquer invalidação de chamados (inclusive a que o
 * tempo real dispara quando chega um aviso) também recarrega a conversa. A consulta periódica é só a rede de segurança.
 */
export const useComments = (ticketId: string) =>
  useQuery({
    queryKey: keys.comments(ticketId),
    queryFn: () => api<TicketComment[]>(`/api/tickets/${ticketId}/comments`),
    refetchInterval: 15_000,
  })

export function useAddComment(ticketId: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (comment: NewComment) =>
      api<TicketComment>(`/api/tickets/${ticketId}/comments`, { method: 'POST', body: comment }),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.comments(ticketId) }),
  })
}

export const useStaff = (enabled: boolean) =>
  useQuery({ queryKey: keys.staff, queryFn: () => api<StaffMember[]>('/api/tickets/staff'), enabled })

export function useCreateTicket() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (ticket: NewTicket) => api<Ticket>('/api/tickets', { method: 'POST', body: ticket }),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.tickets }),
  })
}

export type TicketAction = 'resolve' | 'close' | 'reopen' | { assignTo: string }

export function useTicketAction(id: string) {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (action: TicketAction) =>
      typeof action === 'string'
        ? api<Ticket>(`/api/tickets/${id}/${action}`, { method: 'PUT' })
        : api<Ticket>(`/api/tickets/${id}/assign`, { method: 'PUT', body: { assigneeId: action.assignTo } }),
    onSuccess: (ticket) => {
      qc.setQueryData(keys.ticket(id), ticket)
      void qc.invalidateQueries({ queryKey: keys.tickets })
    },
  })
}

/**
 * Com a conexão em tempo real ATIVA, o servidor empurra as notificações e não precisamos perguntar nada. Sem ela
 * (offline, proxy bloqueando, reconectando), voltamos a consultar a API a cada 15 s: o tempo real é um acelerador,
 * nunca a única forma de receber um aviso.
 */
const pollingWhenNotLive = (status: RealtimeStatus) => (status === 'connected' ? false : 15_000)

export function useNotifications() {
  const status = useRealtimeStatus()
  return useQuery({
    queryKey: keys.notifications,
    queryFn: () => api<AppNotification[]>('/api/notifications'),
    refetchInterval: pollingWhenNotLive(status),
  })
}

/** Contador do menu: as não lidas. Ao vivo pelo SignalR; consulta periódica só como reserva. */
export function useUnreadCount() {
  const status = useRealtimeStatus()
  return useQuery({
    queryKey: keys.unread,
    queryFn: () => api<AppNotification[]>('/api/notifications?unread=true'),
    refetchInterval: pollingWhenNotLive(status),
    select: (list) => list.length,
  })
}

export function useMarkRead() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api(`/api/notifications/${id}/read`, { method: 'PUT' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.notifications }),
  })
}

export const useUsers = (enabled = true) =>
  useQuery({ queryKey: keys.users, queryFn: () => api<User[]>('/api/users'), enabled })

export function useCreateUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (user: NewUser) => api<User>('/api/users', { method: 'POST', body: user }),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.users }),
  })
}

export const useTenantProfile = () =>
  useQuery({ queryKey: keys.tenant, queryFn: () => api<TenantProfile>('/api/tenants/me'), retry: false })
