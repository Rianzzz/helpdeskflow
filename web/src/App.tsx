import { Link, Navigate, Route, Routes } from 'react-router-dom'
import { FullPageSpinner, PublicOnly, RequireAuth, RequireRole } from './components/guards'
import { Layout } from './components/Layout'
import { Button } from './components/ui'
import { useAuth } from './lib/auth'
import { CompanyPage } from './pages/CompanyPage'
import { LoginPage } from './pages/LoginPage'
import { NotificationsPage } from './pages/NotificationsPage'
import { RegisterPage, RegistrationStatusPage } from './pages/RegisterPage'
import { TicketDetailPage } from './pages/TicketDetailPage'
import { TicketsPage } from './pages/TicketsPage'
import { UsersPage } from './pages/UsersPage'

function NotFound() {
  const { user, restoring } = useAuth()
  if (restoring) return <FullPageSpinner />

  return (
    <div className="flex h-full flex-col items-center justify-center gap-3 px-4 text-center">
      <p className="text-5xl font-semibold text-brand-600">404</p>
      <p className="text-slate-600">Não encontramos esta página.</p>
      <Link to={user ? '/tickets' : '/login'}>
        <Button>Voltar ao início</Button>
      </Link>
    </div>
  )
}

function Home() {
  const { user, restoring } = useAuth()
  if (restoring) return <FullPageSpinner />
  return <Navigate to={user ? '/tickets' : '/login'} replace />
}

export default function App() {
  return (
    <Routes>
      {/* Visitantes */}
      <Route element={<PublicOnly />}>
        <Route path="/login" element={<LoginPage />} />
        <Route path="/register" element={<RegisterPage />} />
        <Route path="/register/status/:tenantId" element={<RegistrationStatusPage />} />
      </Route>

      {/* Área autenticada */}
      <Route element={<RequireAuth />}>
        <Route element={<Layout />}>
          <Route path="/tickets" element={<TicketsPage />} />
          <Route path="/tickets/:id" element={<TicketDetailPage />} />
          <Route path="/notifications" element={<NotificationsPage />} />
          <Route path="/company" element={<CompanyPage />} />
          <Route element={<RequireRole roles={['Admin']} />}>
            <Route path="/users" element={<UsersPage />} />
          </Route>
        </Route>
      </Route>

      <Route path="/" element={<Home />} />
      <Route path="*" element={<NotFound />} />
    </Routes>
  )
}
