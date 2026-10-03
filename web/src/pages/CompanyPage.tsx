import { Badge, Card, ErrorBlock, LoadingBlock, PageHeader } from '../components/ui'
import { errorMessage, formatDateTime } from '../lib/format'
import { useTenantProfile } from '../lib/queries'

export function CompanyPage() {
  const profile = useTenantProfile()

  return (
    <>
      <PageHeader title="Empresa" subtitle="Dados e plano da sua conta." />

      <Card>
        {profile.isPending ? (
          <LoadingBlock />
        ) : profile.isError ? (
          <ErrorBlock message={errorMessage(profile.error, 'Não foi possível carregar os dados da empresa.')} onRetry={() => void profile.refetch()} />
        ) : (
          <dl className="grid gap-6 p-6 sm:grid-cols-2">
            <Item label="Nome" value={profile.data.name} />
            <Item label="Plano" value={<Badge tone="brand">{profile.data.plan}</Badge>} />
            <Item label="Limite de usuários" value={String(profile.data.maxUsers)} />
            <Item label="Cliente desde" value={formatDateTime(profile.data.createdAt)} />
          </dl>
        )}
      </Card>
    </>
  )
}

function Item({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <div>
      <dt className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</dt>
      <dd className="mt-1 text-sm text-slate-900">{value}</dd>
    </div>
  )
}
