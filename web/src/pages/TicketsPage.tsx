import { useMemo, useState, type FormEvent } from 'react'
import { Link } from 'react-router-dom'
import { Button, Card, EmptyState, ErrorBlock, Input, LoadingBlock, Modal, PageHeader, PriorityBadge, Select, StatusBadge, Textarea, useToast } from '../components/ui'
import { useAuth } from '../lib/auth'
import { errorMessage, priorityLabel, statusLabel, ticketRef, timeAgo } from '../lib/format'
import { useCreateTicket, useTickets } from '../lib/queries'
import { filterTickets } from '../lib/tickets'
import type { TicketPriority, TicketStatus } from '../lib/types'

// Larguras fixas das colunas: o cabeçalho e as linhas usam as mesmas, então tudo alinha.
// (Só a partir de "md": no celular as colunas não existem, cada informação ocupa o que precisa.)
const col = {
  priority: 'shrink-0 md:w-24',
  ref: 'shrink-0 md:w-14',
  person: 'shrink-0 md:w-32',
  status: 'shrink-0 md:w-28',
  time: 'shrink-0 md:w-14',
}

const person = `order-last truncate text-xs md:order-none md:text-[13px] ${col.person}`

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
        <div className="flex flex-col gap-2 border-b border-slate-200 px-4 pt-1 lg:flex-row lg:items-center lg:justify-between lg:gap-4 lg:pt-0">
          <div role="tablist" aria-label="Filtrar por status" className="-mb-px flex flex-wrap gap-x-5">
            {statusTabs.map((tab) => (
              <button
                key={tab.value}
                role="tab"
                aria-selected={status === tab.value}
                onClick={() => setStatus(tab.value)}
                className={`border-b-2 py-2.5 text-[13px] font-medium transition-colors ${
                  status === tab.value ? 'border-brand-600 text-slate-900' : 'border-transparent text-slate-500 hover:text-slate-800'
                }`}
              >
                {tab.label}
                <span className="ml-1.5 font-mono text-xs text-slate-500">{counts(tab.value)}</span>
              </button>
            ))}
          </div>
          <input
            type="search"
            aria-label="Buscar chamados"
            placeholder={isStaff ? 'Buscar por título ou solicitante…' : 'Buscar por título…'}
            value={search}
            onChange={(e) => setSearch(e.target.value)}
            className="mb-2 w-full rounded-md border-0 bg-white px-2.5 py-1.5 text-[13px] ring-1 ring-inset ring-slate-300 placeholder:text-slate-500 focus:ring-2 focus:ring-brand-600 lg:mb-0 lg:w-72"
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
          <>
            {/* Cabeçalho das colunas (só para quem enxerga: a lista já se explica para leitores de tela). */}
            <div aria-hidden="true" className="hidden items-center gap-3 border-b border-slate-100 px-4 py-1.5 text-[11px] font-medium uppercase tracking-wider text-slate-500 md:flex">
              <span className={col.priority}>Prioridade</span>
              <span className={col.ref}>Ref.</span>
              <span className="min-w-0 flex-1">Título</span>
              {isStaff && <span className={col.person}>Solicitante</span>}
              <span className={col.person}>Responsável</span>
              <span className={col.status}>Status</span>
              <span className={`${col.time} text-right`}>Aberto</span>
            </div>
            <ul className="divide-y divide-slate-100">
              {visible.map((t) => (
                <li key={t.id}>
                  <Link
                    to={`/tickets/${t.id}`}
                    className="flex flex-wrap items-center gap-x-3 gap-y-1 px-4 py-2.5 text-[13px] transition-colors hover:bg-slate-50 md:flex-nowrap"
                  >
                    <PriorityBadge priority={t.priority} className={col.priority} />
                    <span className={`${col.ref} font-mono text-xs text-slate-500`}>{ticketRef(t.id)}</span>
                    <span className="order-last flex min-w-0 basis-full items-center gap-2 md:order-none md:basis-0 md:flex-1">
                      <span className="truncate font-medium text-slate-900">{t.title}</span>
                      {t.slaBreachedAt && (
                        <span className="shrink-0 rounded-sm bg-red-50 px-1.5 py-0.5 text-[11px] font-medium text-red-700" title="O prazo de primeiro atendimento estourou">
                          SLA estourado
                        </span>
                      )}
                    </span>
                    {/* Pessoas: colunas no desktop; no celular descem para uma linha abaixo do título (mesmo elemento). */}
                    {isStaff && (
                      <span className={`${person} text-slate-600`}>{t.requesterId === user?.id ? 'Você' : (t.requesterName ?? 'Usuário')}</span>
                    )}
                    <span className={`${person} ${t.assigneeName ? 'text-slate-600' : 'text-slate-500'}`}>{t.assigneeName ?? 'sem responsável'}</span>
                    <span className={col.status}>
                      <StatusBadge status={t.status} />
                    </span>
                    <span className={`${col.time} ml-auto text-right text-xs text-slate-500 md:ml-0`}>{timeAgo(t.createdAt)}</span>
                  </Link>
                </li>
              ))}
            </ul>
          </>
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
