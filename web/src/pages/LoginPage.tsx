import { useState, type FormEvent } from 'react'
import { Link, useLocation, useNavigate } from 'react-router-dom'
import { Alert, Button, Input } from '../components/ui'
import { ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { AuthShell } from './AuthShell'

export function LoginPage() {
  const { login } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const state = location.state as { from?: string; email?: string; justRegistered?: boolean } | null

  const [email, setEmail] = useState(state?.email ?? '')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setSubmitting(true)
    try {
      await login(email.trim(), password)
      navigate(state?.from ?? '/tickets', { replace: true })
    } catch (e) {
      // Mensagem propositalmente genérica para 401 (o servidor também não diz se foi o e-mail ou a senha).
      setError(e instanceof ApiError && e.status === 401 ? 'E-mail ou senha incorretos, ou conta temporariamente bloqueada.' : e instanceof ApiError ? e.message : 'Não foi possível entrar. Verifique sua conexão.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <AuthShell title="Entrar" subtitle="Acesse a central de chamados da sua empresa.">
      <form onSubmit={handleSubmit} className="space-y-4" noValidate>
        {state?.justRegistered && <Alert tone="green">Empresa criada com sucesso! Entre com o e-mail e a senha que você cadastrou.</Alert>}
        {error && <Alert>{error}</Alert>}

        <Input
          label="E-mail"
          type="email"
          autoComplete="username"
          required
          value={email}
          onChange={(e) => setEmail(e.target.value)}
          placeholder="voce@empresa.com"
        />
        <Input
          label="Senha"
          type="password"
          autoComplete="current-password"
          required
          value={password}
          onChange={(e) => setPassword(e.target.value)}
        />

        <Button type="submit" loading={submitting} className="w-full" disabled={!email || !password}>
          Entrar
        </Button>
      </form>

      <p className="mt-6 text-center text-sm text-slate-600">
        Sua empresa ainda não usa o HelpDeskFlow?{' '}
        <Link to="/register" className="font-medium text-brand-600 hover:text-brand-700">
          Criar empresa
        </Link>
      </p>
    </AuthShell>
  )
}
