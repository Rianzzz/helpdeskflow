import { useState, type ReactNode } from 'react'
import { NavLink, Outlet, useNavigate } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import { roleLabel } from '../lib/format'
import { useUnreadCount } from '../lib/queries'
import { useRealtimeStatus, type RealtimeStatus } from '../lib/realtime'
import { useRealtimeNotifications } from '../lib/useRealtimeNotifications'
import type { Role } from '../lib/types'
import { Badge } from './ui'

interface NavItem {
  to: string
  label: string
  icon: ReactNode
  roles?: Role[]
  badge?: number
}

const icon = (path: string) => (
  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.7" className="h-5 w-5" aria-hidden="true">
    <path strokeLinecap="round" strokeLinejoin="round" d={path} />
  </svg>
)

const icons = {
  tickets: icon('M16.5 6v.75m0 3v.75m0 3v.75m0 3V18m-9-5.25h5.25M7.5 15h3M3.375 5.25c-.621 0-1.125.504-1.125 1.125v3.026a2.999 2.999 0 0 1 0 5.198v3.026c0 .621.504 1.125 1.125 1.125h17.25c.621 0 1.125-.504 1.125-1.125v-3.026a2.999 2.999 0 0 1 0-5.198V6.375c0-.621-.504-1.125-1.125-1.125H3.375Z'),
  bell: icon('M14.857 17.082a23.848 23.848 0 0 0 5.454-1.31A8.967 8.967 0 0 1 18 9.75V9A6 6 0 0 0 6 9v.75a8.967 8.967 0 0 1-2.312 6.022c1.733.64 3.56 1.085 5.455 1.31m5.714 0a24.255 24.255 0 0 1-5.714 0m5.714 0a3 3 0 1 1-5.714 0'),
  users: icon('M15 19.128a9.38 9.38 0 0 0 2.625.372 9.337 9.337 0 0 0 4.121-.952 4.125 4.125 0 0 0-7.533-2.493M15 19.128v-.003c0-1.113-.285-2.16-.786-3.07M15 19.128v.106A12.318 12.318 0 0 1 8.624 21c-2.331 0-4.512-.645-6.374-1.766l-.001-.109a6.375 6.375 0 0 1 11.964-3.07M12 6.375a3.375 3.375 0 1 1-6.75 0 3.375 3.375 0 0 1 6.75 0Zm8.25 2.25a2.625 2.625 0 1 1-5.25 0 2.625 2.625 0 0 1 5.25 0Z'),
  building: icon('M2.25 21h19.5m-18-18v18m10.5-18v18m6-13.5V21M6.75 6.75h.75m-.75 3h.75m-.75 3h.75m3-6h.75m-.75 3h.75m-.75 3h.75M6.75 21v-3.375c0-.621.504-1.125 1.125-1.125h2.25c.621 0 1.125.504 1.125 1.125V21M3 3h12m-.75 4.5H21m-3.75 3.75h.008v.008h-.008v-.008Zm0 3h.008v.008h-.008v-.008Zm0 3h.008v.008h-.008v-.008Z'),
}

/** Mostra, sem alarde, se as notificações estão chegando ao vivo ou se estamos no modo de reserva (consulta periódica). */
function LiveIndicator({ status }: { status: RealtimeStatus }) {
  const view = {
    connected: { dot: 'bg-emerald-500', label: 'Ao vivo' },
    connecting: { dot: 'bg-amber-500', label: 'Conectando…' },
    reconnecting: { dot: 'bg-amber-500', label: 'Reconectando…' },
    offline: { dot: 'bg-slate-400', label: 'Atualizando a cada 15 s' },
  }[status]

  return (
    <p className="mt-2 flex items-center gap-1.5 text-xs text-slate-600" data-testid="live-indicator" data-status={status}>
      <span aria-hidden="true" className={`h-2 w-2 rounded-full ${view.dot}`} />
      {view.label}
    </p>
  )
}

export function Layout() {
  const { user, logout } = useAuth()
  const navigate = useNavigate()
  const unread = useUnreadCount()
  const live = useRealtimeStatus()
  useRealtimeNotifications() // abre a conexão em tempo real enquanto a pessoa está logada
  const [menuOpen, setMenuOpen] = useState(false)

  const items: NavItem[] = [
    { to: '/tickets', label: 'Chamados', icon: icons.tickets },
    { to: '/notifications', label: 'Notificações', icon: icons.bell, badge: unread.data },
    { to: '/users', label: 'Usuários', icon: icons.users, roles: ['Admin'] },
    { to: '/company', label: 'Empresa', icon: icons.building },
  ]
  const visible = items.filter((i) => !i.roles || (user && i.roles.includes(user.role)))

  async function handleLogout() {
    await logout()
    navigate('/login', { replace: true })
  }

  const nav = (
    <nav aria-label="Principal" className="flex-1 space-y-1 px-3">
      {visible.map((item) => (
        <NavLink
          key={item.to}
          to={item.to}
          onClick={() => setMenuOpen(false)}
          className={({ isActive }) =>
            `flex items-center gap-3 rounded-lg px-3 py-2 text-sm font-medium transition-colors ${
              isActive ? 'bg-brand-50 text-brand-700' : 'text-slate-600 hover:bg-slate-100 hover:text-slate-900'
            }`
          }
        >
          {item.icon}
          <span className="flex-1">{item.label}</span>
          {item.badge ? (
            <span className="rounded-full bg-brand-600 px-2 py-0.5 text-xs font-semibold text-white" aria-label={`${item.badge} não lidas`}>
              {item.badge}
            </span>
          ) : null}
        </NavLink>
      ))}
    </nav>
  )

  const account = (
    <div className="border-t border-slate-200 p-4">
      <p className="truncate text-sm font-medium text-slate-900">{user?.name}</p>
      <p className="truncate text-xs text-slate-500">{user?.email}</p>
      <LiveIndicator status={live} />
      <div className="mt-2 flex items-center justify-between">
        {user && <Badge tone="brand">{roleLabel[user.role]}</Badge>}
        <button onClick={handleLogout} className="text-sm font-medium text-slate-600 hover:text-slate-900">
          Sair
        </button>
      </div>
    </div>
  )

  const brand = (
    <div className="flex items-center gap-2.5 px-6 py-5">
      <span className="flex h-8 w-8 items-center justify-center rounded-lg bg-brand-600 text-white">
        <svg viewBox="0 0 24 24" fill="currentColor" className="h-5 w-5" aria-hidden="true">
          <path d="M4.5 8.25A3.75 3.75 0 0 1 8.25 4.5h7.5a3.75 3.75 0 0 1 3.75 3.75v4.5a3.75 3.75 0 0 1-3.75 3.75h-3.4l-4.1 3v-3.04A3.75 3.75 0 0 1 4.5 12.75z" />
        </svg>
      </span>
      <span className="text-lg font-semibold tracking-tight text-slate-900">HelpDeskFlow</span>
    </div>
  )

  return (
    <div className="flex h-full flex-col md:flex-row">
      {/* Menu lateral (telas grandes) */}
      <aside className="hidden w-64 shrink-0 flex-col border-r border-slate-200 bg-white md:flex">
        {brand}
        {nav}
        {account}
      </aside>

      {/* Barra superior (celular) */}
      <header className="flex items-center justify-between border-b border-slate-200 bg-white md:hidden">
        {brand}
        <button
          onClick={() => setMenuOpen((o) => !o)}
          aria-expanded={menuOpen}
          aria-controls="mobile-menu"
          className="mr-4 rounded-lg p-2 text-slate-600 hover:bg-slate-100"
        >
          <span className="sr-only">Abrir menu</span>
          <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.8" className="h-6 w-6" aria-hidden="true">
            <path strokeLinecap="round" d="M4 7h16M4 12h16M4 17h16" />
          </svg>
        </button>
      </header>
      {menuOpen && (
        <div id="mobile-menu" className="border-b border-slate-200 bg-white pb-2 md:hidden">
          {nav}
          {account}
        </div>
      )}

      <main className="flex-1 overflow-y-auto">
        <div className="mx-auto max-w-5xl px-4 py-6 sm:px-6 lg:px-8 lg:py-8">
          <Outlet />
        </div>
      </main>
    </div>
  )
}
