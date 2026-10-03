import { useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { Button, Card, EmptyState, ErrorBlock, Input, LoadingBlock, Modal, PageHeader, PriorityBadge, Select, StatusBadge, Textarea, useToast } from '../components/ui'
import { useAuth } from '../lib/auth'
import { errorMessage, priorityLabel, statusLabel, timeAgo } from '../lib/format'
import { useCreateTicket, useTickets } from '../lib/queries'
import { filterTickets } from '../lib/tickets'
import type { TicketPriority, TicketStatus } from '../lib/types'

const statusTabs: { value: TicketStatus | 'all'; label: string }[] = [
  { value: 'all', label: 'Todos' },
  { value: 'Open', label: statusLabel.Open },
  { value: 'InProgress', label: statusLabel.InProgress },
  { value: 'Resolved', label: statusLabel.Resolved },
  { value: 'Closed', label: statusLabel.Closed },
]

export function TicketsPage() {
  const { user, hasRole } = useAuth()
  const tickets = useTickets()
  const [status, setStatus] = useState<TicketStatus | 'all'>('all')
  const [search, setSearch] = useState('')
  const [creating, setCreating] = useState(false)

  const isStaff = hasRole('Admin', 'Agent')
  const visible = useMemo(() => filterTickets(tickets.data ?? [], status, search), [tickets.data, status, search])
  const counts = useMemo(() => {
    const all = tickets.data ?? []
    return (value: TicketStatus | 'all') => (value === 'all' ? all.length : all.filter((t) => t.status === value).length)
  }, [tickets.data])

  return (
    <>
      <PageHeader
        title="Chamados"
        subtitle={isStaff ? 'Todos os chamados da sua empresa.' : 'Os chamados que você abriu.'}
        actions={<Button onClick={() => setCreating(true)}>Novo chamado</Button>}
      />

      <Card>
        <div className="flex flex-col gap-3 border-b border-slate-200 p-4 lg:flex-row lg:items-center lg:justify-between">
          <div role="tablist" aria-label="Filtrar por status" className="flex flex-wrap gap-1">
            {statusTabs.map((tab) => (
              <button
                key={tab.value}
                role="tab"
                aria-selected={status === tab.value}
                onClick={() => setStatus(tab.value)}
                className={`rounded-lg px-3 py-1.5 text-sm font-medium transition-colors ${
                  status === tab.value ? 'bg-brand-50 text-brand-700' : 'text-slate-600 hover:bg-slate-100'
                }`}
              >
                {tab.label}
                <span className="ml-1.5 text-xs text-slate-400">{counts(tab.value)}</span>
              </button>
            ))}
          </div>
          <input
            type="search"
            aria-label="Buscar chamados"
            placeholder={isStaff ? 'Buscar por título ou solicitante…' : 'Buscar por título…'}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="w-full rounded-lg border-0 bg-white px-3 py-2 text-sm shadow-sm ring-1 ring-slate-300 placeholder:text-slate-400 focus:ring-2 focus:ring-brand-600 lg:w-72"
          />
        </div>

        {tickets.isPending ? (
          <LoadingBlock label="Carregando chamados…" />
        ) : tickets.isError ? (
          <ErrorBlock message={errorMessage(tickets.error, 'Não foi possível carregar os chamados.')} onRetry={() => void tickets.refetch()} />
        ) : visible.length === 0 ? (
          <EmptyState
            title={tickets.data.length === 0 ? 'Nenhum chamado por aqui ainda' : 'Nenhum chamado encontrado'}
            description={tickets.data.length === 0 ? 'Quando alguém abrir um chamado, ele aparece nesta lista.' : 'Tente outro filtro ou outra busca.'}
            action={tickets.data.length === 0 ? <Button onClick={() => setCreating(true)}>Abrir o primeiro chamado</Button> : undefined}
          />
        ) : (
          <ul className="divide-y divide-slate-100">
            {visible.map((t) => (
              <li key={t.id}>
                <Link to={`/tickets/${t.id}`} className="flex flex-col gap-2 px-4 py-4 transition-colors hover:bg-slate-50 sm:flex-row sm:items-center sm:gap-4">
                  <div className="min-w-0 flex-1">
                    <p className="truncate text-sm font-medium text-slate-900">{t.title}</p>
                    <p className="mt-0.5 truncate text-xs text-slate-500">
                      {isStaff && t.requesterId !== user?.id ? `${t.requesterName ?? 'Usuário'} · ` : ''}
                      aberto {timeAgo(t.createdAt)}
                      {t.assigneeName ? ` · responsável: ${t.assigneeName}` : ' · sem responsável'}
                    </p>
                  </div>
                  <div className="flex shrink-0 items-center gap-2">
                    {t.slaBreachedAt && (
                      <span className="rounded-full bg-red-50 px-2 py-0.5 text-xs font-medium text-red-700 ring-1 ring-inset ring-red-200" title="O prazo de primeiro atendimento estourou">
                        SLA estourado
                      </span>
                    )}
                    <PriorityBadge priority={t.priority} />
                    <StatusBadge status={t.status} />
                  </div>
                </Link>
              </li>
            ))}
          </ul>
        )}
      </Card>

      <NewTicketModal open={creating} onClose={() => setCreating(false)} />
    </>
  )
}

function NewTicketModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateTicket()
  const { notify } = useToast()
  const [title, setTitle] = useState('')
  const [description, setDescription] = useState('')
  const [priority, setPriority] = useState<TicketPriority>('Medium')

  async function handleSubmit(event: FormEvent) {
    event.preventDefault()
    try {
      await create.mutateAsync({ title: title.trim(), description: description.trim(), priority })
      notify('success', 'Chamado aberto! A equipe já foi avisada.')
      setTitle('')
      setDescription('')
      setPriority('Medium')
      onClose()
    } catch (e) {
      notify('error', errorMessage(e, 'Não foi possível abrir o chamado.'))
    }
  }

  return (
    <Modal open={open} onClose={onClose} title="Novo chamado">
      <form onSubmit={handleSubmit} className="space-y-4">
        <Input label="Título" required maxLength={200} value={title} onChange={(e) => setTitle(e.target.value)} placeholder="Ex.: A impressora da sala 3 não funciona" />
        <Textarea label="Descrição" maxLength={4000} value={description} onChange={(e) => setDescription(e.target.value)} hint="Conte o que aconteceu e o que você já tentou." />
        <Select label="Prioridade" value={priority} onChange={(e) => setPriority(e.target.value as TicketPriority)}>
          {(['Low', 'Medium', 'High', 'Urgent'] as const).map((p) => (
            <option key={p} value={p}>
              {priorityLabel[p]}
            </option>
          ))}
        </Select>
        <div className="flex justify-end gap-2 pt-2">
          <Button type="button" variant="secondary" onClick={onClose}>
            Cancelar
          </Button>
          <Button type="submit" loading={create.isPending} disabled={!title.trim()}>
            Abrir chamado
          </Button>
        </div>
      </form>
    </Modal>
  )
}
