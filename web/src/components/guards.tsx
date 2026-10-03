import { Navigate, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../lib/auth'
import type { Role } from '../lib/types'
import { safeRedirectTarget } from '../lib/redirect'
import { Card, EmptyState, Spinner } from './ui'

export function FullPageSpinner() {
  return (
    <div className="flex h-full items-center justify-center text-brand-600">
      <Spinner className="h-8 w-8" />
    </div>
  )
}

/** Só deixa passar quem está logado; senão manda para o login lembrando de onde veio. */
export function RequireAuth() {
  const { user, restoring } = useAuth()
  const location = useLocation()

  if (restoring) return <FullPageSpinner />
  if (!user) return <Navigate to="/login" state={{ from: location.pathname }} replace />
  return <Outlet />
}

/**
 * Telas de visitante (login/cadastro): quem já está logado sai daqui. Vai para a página que tentou abrir antes de
 * ser mandado ao login (guardada em state.from pelo RequireAuth) ou, se não houver, para os chamados.
 * É ESTE componente que redireciona logo após o login, por isso ele precisa respeitar o "de onde veio".
 */
export function PublicOnly() {
  const { user, restoring } = useAuth()
  const location = useLocation()

  if (restoring) return <FullPageSpinner />
  if (user) return <Navigate to={safeRedirectTarget((location.state as { from?: string } | null)?.from)} replace />
  return <Outlet />
}

/**
 * Restringe uma rota por papel. IMPORTANTE: isto é só experiência de uso. A segurança de verdade está no servidor,
 * que valida o papel dentro do token em cada requisição. Esconder um botão nunca protege nada sozinho.
 */
export function RequireRole({ roles }: { roles: Role[] }) {
  const { hasRole } = useAuth()

  if (!hasRole(...roles)) {
    return (
      <Card>
        <EmptyState title="Acesso restrito" description="Você não tem permissão para ver esta página." />
      </Card>
    )
  }
  return <Outlet />
}
