import { useQuery } from '@tanstack/react-query'
import { useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom'
import { Alert, Button, Input, Spinner } from '../components/ui'
import { ApiError, api } from '../lib/api'
import { passwordProblem } from '../lib/validation'
import type { RegisterTenantResponse, TenantStatusResponse } from '../lib/types'
import { AuthShell } from './AuthShell'

export function RegisterPage() {
  const navigate = useNavigate()
  const location = useLocation()
  const retry = location.state as { companyName?: string; adminName?: string; email?: string; reason?: string } | null

  const [companyName, setCompanyName] = useState(retry?.companyName ?? '')
  const [adminName, setAdminName] = useState(retry?.adminName ?? '')
  const [email, setEmail] = useState(retry?.email ?? '')
  const [password, setPassword] = useState('')
  const [touched, setTouched] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  const passwordError = touched ? passwordProblem(password) : null

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setTouched(true)
    setError(null)
    if (passwordProblem(password)) return

    setSubmitting(true)
    try {
      const result = await api<RegisterTenantResponse>('/api/auth/register-tenant', {
        method: 'POST',
        auth: false,
        body: { companyName: companyName.trim(), adminName: adminName.trim(), email: email.trim(), password },
      })
      // 202: o cadastro foi recebido e o provisionamento acontece em segundo plano. Acompanhamos pelo status.
      navigate(`/register/status/${result.tenantId}`, { state: { companyName, adminName, email } })
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'Não foi possível enviar o cadastro. Verifique sua conexão.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <AuthShell title="Criar empresa" subtitle="Cadastre sua empresa e o primeiro administrador.">
      <form onSubmit={handleSubmit} className="space-y-4" noValidate>
        {retry?.reason && <Alert tone="amber">O cadastro anterior não foi concluído: {retry.reason}</Alert>}
        {error && <Alert>{error}</Alert>}

        <Input label="Nome da empresa" required maxLength={150} value={companyName} onChange={(e) => setCompanyName(e.target.value)} />
        <Input label="Seu nome" required maxLength={150} autoComplete="name" value={adminName} onChange={(e) => setAdminName(e.target.value)} />
        <Input label="E-mail" type="email" required autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} />
        <Input
          label="Senha"
          type="password"
          required
          autoComplete="new-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          onBlur={() => setTouched(true)}
          hint="Mínimo de 10 caracteres, com letras e números."
          error={passwordError}
        />

        <Button type="submit" loading={submitting} className="w-full" disabled={!companyName.trim() || !adminName.trim() || !email}>
          Criar empresa
        </Button>
      </form>

      <p className="mt-6 text-center text-sm text-slate-600">
        Já tem conta?{' '}
        <Link to="/login" className="font-medium text-brand-600 hover:text-brand-700">
          Entrar
        </Link>
      </p>
    </AuthShell>
  )
}

/**
 * Acompanha a SAGA de onboarding. O cadastro responde 202 e o provisionamento passa por vários serviços
 * (Identity → Tenants → Identity); aqui consultamos o status a cada 1,5 s até virar Active ou Failed.
 */
export function RegistrationStatusPage() {
  const { tenantId } = useParams()
  const navigate = useNavigate()
  const location = useLocation()
  const form = location.state as { companyName?: string; adminName?: string; email?: string } | null

  const status = useQuery({
    queryKey: ['tenant-status', tenantId],
    queryFn: () => api<TenantStatusResponse>(`/api/auth/tenants/${tenantId}/status`, { auth: false }),
    refetchInterval: (query) => (query.state.data?.status === 'Provisioning' || !query.state.data ? 1500 : false),
    retry: false,
  })

  const current = status.data?.status

  if (status.isError) {
    return (
      <AuthShell title="Cadastro não encontrado">
        <Alert>Não encontramos este cadastro. Ele pode ter expirado.</Alert>
        <Link to="/register" className="mt-4 block text-center text-sm font-medium text-brand-600">
          Começar de novo
        </Link>
      </AuthShell>
    )
  }

  if (current === 'Active') {
    return (
      <AuthShell title="Empresa pronta!" subtitle="Tudo certo: sua empresa foi criada e ativada.">
        <Alert tone="green">Você já pode entrar com o e-mail e a senha que cadastrou.</Alert>
        <Button className="mt-5 w-full" onClick={() => navigate('/login', { state: { email: form?.email, justRegistered: true } })}>
          Entrar agora
        </Button>
      </AuthShell>
    )
  }

  if (current === 'Failed') {
    return (
      <AuthShell title="Não foi possível criar a empresa">
        <Alert tone="amber">{status.data?.failureReason ?? 'O cadastro foi recusado.'}</Alert>
        <p className="mt-3 text-sm text-slate-500">Nada foi criado e o seu e-mail está livre para uma nova tentativa.</p>
        <Button
          className="mt-5 w-full"
          onClick={() => navigate('/register', { state: { ...form, reason: status.data?.failureReason ?? undefined } })}
        >
          Tentar novamente
        </Button>
      </AuthShell>
    )
  }

  return (
    <AuthShell title="Criando a sua empresa…" subtitle="Isso leva só alguns segundos.">
      <div className="flex flex-col items-center gap-4 py-4 text-brand-600">
        <Spinner className="h-10 w-10" />
        <ol className="space-y-1.5 text-sm text-slate-600" aria-live="polite">
          <li>✓ Cadastro recebido</li>
          <li>… Validando o nome da empresa e criando o plano</li>
          <li className="text-slate-400">○ Ativando o acesso</li>
        </ol>
      </div>
    </AuthShell>
  )
}
