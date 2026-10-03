import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { api } from './api'
import type { AppNotification, NewTicket, NewUser, StaffMember, TenantProfile, Ticket, User } from './types'

// Chaves de cache centralizadas: quem altera dados invalida a chave certa.
export const keys = {
  tickets: ['tickets'] as const,
  ticket: (id: string) => ['tickets', id] as const,
  staff: ['staff'] as const,
  notifications: ['notifications'] as const,
  unread: ['notifications', 'unread'] as const,
  users: ['users'] as const,
  tenant: ['tenant'] as const,
}

export const useTickets = () => useQuery({ queryKey: keys.tickets, queryFn: () => api<Ticket[]>('/api/tickets') })

export const useTicket = (id: string) =>
  useQuery({ queryKey: keys.ticket(id), queryFn: () => api<Ticket>(`/api/tickets/${id}`) })

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

export const useNotifications = () =>
  useQuery({ queryKey: keys.notifications, queryFn: () => api<AppNotification[]>('/api/notifications') })

/** Contador do sino: consulta só as não lidas, a cada 15 s (simples e suficiente; tempo real seria com SignalR). */
export const useUnreadCount = () =>
  useQuery({
    queryKey: keys.unread,
    queryFn: () => api<AppNotification[]>('/api/notifications?unread=true'),
    refetchInterval: 15_000,
    select: (list) => list.length,
  })

export function useMarkRead() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) => api(`/api/notifications/${id}/read`, { method: 'PUT' }),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.notifications }),
  })
}

export const useUsers = () => useQuery({ queryKey: keys.users, queryFn: () => api<User[]>('/api/users') })

export function useCreateUser() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (user: NewUser) => api<User>('/api/users', { method: 'POST', body: user }),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.users }),
  })
}

export const useTenantProfile = () =>
  useQuery({ queryKey: keys.tenant, queryFn: () => api<TenantProfile>('/api/tenants/me'), retry: false })
