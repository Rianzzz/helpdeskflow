import { Link } from 'react-router-dom'
import { Badge, Card, ErrorBlock, LoadingBlock, PageHeader } from '../components/ui'
import { useAuth } from '../lib/auth'
import { errorMessage, formatDateTime } from '../lib/format'
import { planUsage, type PlanUsage } from '../lib/plan'
import { useTenantProfile, useUsers } from '../lib/queries'

export function CompanyPage() {
  const { hasRole } = useAuth()
  const isAdmin = hasRole('Admin')
  const profile = useTenantProfile()
  const users = useUsers(isAdmin) // só o administrador pode listar usuários (e, portanto, ver o uso do plano)

  const usage = isAdmin && users.data && profile.data ? planUsage(users.data.length, profile.data.maxUsers) : null

  return (
    <>
      <PageHeader title="Empresa" subtitle="Dados e plano da sua conta." />

      <Card>
        {profile.isPending ? (
          <LoadingBlock />
        ) : profile.isError ? (
          <ErrorBlock message={errorMessage(profile.error, 'Não foi possível carregar os dados da empresa.')} onRetry={() => void profile.refetch()} />
        ) : (
          <>
            <dl className="grid gap-6 p-6 sm:grid-cols-2">
              <Item label="Nome" value={profile.data.name} />
              <Item label="Plano" value={<Badge tone="brand">{profile.data.plan}</Badge>} />
              <Item label="Limite de usuários" value={String(profile.data.maxUsers)} />
              <Item label="Cliente desde" value={formatDateTime(profile.data.createdAt)} />
            </dl>

            {usage && <UsageBar usage={usage} />}
          </>
        )}
      </Card>
    </>
  )
}

const barTone = { ok: 'bg-brand-600', warning: 'bg-amber-500', full: 'bg-red-600' } as const

function UsageBar({ usage }: { usage: PlanUsage }) {
  return (
    <div className="border-t border-slate-200 p-6">
      <div className="mb-2 flex items-baseline justify-between">
        <h2 className="text-sm font-semibold text-slate-900">Uso do plano</h2>
        <p className="text-sm text-slate-600">
          {usage.used} de {usage.limit} usuários
        </p>
      </div>

      <div
        role="progressbar"
        aria-label="Usuários usados no plano"
        aria-valuemin={0}
        aria-valuemax={usage.limit}
        aria-valuenow={usage.used}
        className="h-2.5 overflow-hidden rounded-full bg-slate-100"
      >
        <div className={`h-full rounded-full transition-all ${barTone[usage.level]}`} style={{ width: `${usage.percent}%` }} />
      </div>

      <p className="mt-3 text-sm text-slate-600">
        {usage.isFull ? (
          <>Plano lotado. Para adicionar mais pessoas, faça upgrade do plano.</>
        ) : (
          <>
            Você ainda pode adicionar {usage.remaining} {usage.remaining === 1 ? 'pessoa' : 'pessoas'}.{' '}
            <Link to="/users" className="font-medium text-brand-600 hover:text-brand-700">
              Gerenciar usuários
            </Link>
          </>
        )}
      </p>
    </div>
  )
}

function Item({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <dt className="text-[11px] font-medium uppercase tracking-wider text-slate-500">{label}</dt>
      <dd className="mt-1 text-sm text-slate-900">{value}</dd>
    </div>
  )
}
