// Tipos que espelham as respostas da API (gateway). Mantidos à mão: a API é pequena e estável.

export type Role = 'Admin' | 'Agent' | 'Customer'
export type TicketStatus = 'Open' | 'InProgress' | 'Resolved' | 'Closed'
export type TicketPriority = 'Low' | 'Medium' | 'High' | 'Urgent'

export interface User {
  id: string
  tenantId: string
  name: string
  email: string
  role: Role
  isActive: boolean
}

export interface AuthResponse {
  accessToken: string
  tokenType: string
  expiresInSeconds: number
  refreshToken: string
  user: User
}

export type TenantStatus = 'Provisioning' | 'Active' | 'Failed'

export interface RegisterTenantResponse {
  tenantId: string
  status: TenantStatus
}

export interface TenantStatusResponse {
  tenantId: string
  status: TenantStatus
  failureReason: string | null
}

export interface Ticket {
  id: string
  requesterId: string
  requesterName: string | null
  title: string
  description: string
  status: TicketStatus
  priority: TicketPriority
  assigneeId: string | null
  assigneeName: string | null
  createdAt: string
  closedAt: string | null
  slaBreachedAt: string | null
}

export interface StaffMember {
  id: string
  name: string
  role: Role
}

export interface AppNotification {
  id: string
  subject: string
  body: string
  createdAt: string
  readAt: string | null
}

export interface TenantProfile {
  id: string
  name: string
  plan: string
  maxUsers: number
  createdAt: string
}

export interface NewTicket {
  title: string
  description: string
  priority: TicketPriority
}

export interface NewUser {
  name: string
  email: string
  password: string
  role: Role
}
