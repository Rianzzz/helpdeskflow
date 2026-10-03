import { useState, type FormEvent } from 'react'
import { Badge, Button, Card, EmptyState, ErrorBlock, Input, LoadingBlock, Modal, PageHeader, Select, useToast } from '../components/ui'
import { ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { errorMessage, roleLabel } from '../lib/format'
import { useCreateUser, useUsers } from '../lib/queries'
import type { Role } from '../lib/types'
import { passwordProblem } from '../lib/validation'

const roleTone = { Admin: 'brand', Agent: 'sky', Customer: 'slate' } as const

export function UsersPage() {
  const { user: me } = useAuth()
  const users = useUsers()
  const [creating, setCreating] = useState(false)

  return (
    <>
      <PageHeader
        title="Usuários"
        subtitle="Quem tem acesso à sua empresa."
        actions={<Button onClick={() => setCreating(true)}>Novo usuário</Button>}
      />

      <Card>
        {users.isPending ? (
          <LoadingBlock label="Carregando usuários…" />
        ) : users.isError ? (
          <ErrorBlock message={errorMessage(users.error)} onRetry={() => void users.refetch()} />
        ) : users.data.length === 0 ? (
          <EmptyState title="Nenhum usuário" />
        ) : (
          <ul className="divide-y divide-slate-100">
            {users.data.map((u) => (
              <li key={u.id} className="flex items-center gap-4 px-5 py-4">
                <span className="flex h-9 w-9 shrink-0 items-center justify-center rounded-full bg-brand-100 text-sm font-semibold text-brand-700" aria-hidden="true">
                  {u.name.charAt(0).toUpperCase()}
                </span>
                <div className="min-w-0 flex-1">
                  <p className="truncate text-sm font-medium text-slate-900">
                    {u.name}
                    {u.id === me?.id && <span className="ml-2 text-xs font-normal text-slate-500">(você)</span>}
                  </p>
                  <p className="truncate text-xs text-slate-500">{u.email}</p>
                </div>
                <Badge tone={roleTone[u.role]}>{roleLabel[u.role]}</Badge>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <NewUserModal open={creating} onClose={() => setCreating(false)} />
    </>
  )
}

function NewUserModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateUser()
  const { notify } = useToast()
  const [name, setName] = useState('')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState<Role>('Agent')
  const [touched, setTouched] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const passwordError = touched ? passwordProblem(password) : null

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    setTouched(true)
    setError(null)
    if (passwordProblem(password)) return

    try {
      await create.mutateAsync({ name: name.trim(), email: email.trim(), password, role })
      notify('success', `${name.trim()} foi adicionado(a). Em instantes já poderá ser escolhido(a) como responsável.`)
      setName('')
      setEmail('')
      setPassword('')
      setRole('Agent')
      setTouched(false)
      onClose()
    } catch (e) {
      setError(e instanceof ApiError ? e.message : errorMessage(e))
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="Novo usuário">
      <form onSubmit={handleSubmit} className="space-y-4" noValidate>
        {error && (
          <p role="alert" className="rounded-lg bg-red-50 px-3.5 py-3 text-sm text-red-800 ring-1 ring-inset ring-red-200">
            {error}
          </p>
        )}
        <Input label="Nome" required maxLength={150} value={name} onChange={(e) => setName(e.target.value)} />
        <Input label="E-mail" type="email" required autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} />
        <Input
          label="Senha inicial"
          type="password"
          required
          autoComplete="new-password"
          value={password}
          onChange={(e) => setPassword(e.target.value)}
          onBlur={() => setTouched(true)}
          hint="Mínimo de 10 caracteres, com letras e números."
          error={passwordError}
        />
        <Select label="Papel" value={role} onChange={(e) => setRole(e.target.value as Role)}>
          <option value="Agent">Atendente: responde e resolve chamados</option>
          <option value="Customer">Cliente: abre e acompanha os próprios chamados</option>
          <option value="Admin">Administrador: tudo, inclusive usuários</option>
        </Select>
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="secondary" onClick={onClose}>
            Cancelar
          </Button>
          <Button type="submit" loading={create.isPending} disabled={!name.trim() || !email}>
            Adicionar
          </Button>
        </div>
      </form>
    </Modal>
  )
}
