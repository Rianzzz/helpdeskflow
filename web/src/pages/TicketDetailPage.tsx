import { Link, useParams } from 'react-router-dom'
import { useState } from 'react'
import { CommentThread } from '../components/CommentThread'
import { Alert, Button, Card, ErrorBlock, LoadingBlock, PriorityBadge, Select, StatusBadge, useToast } from '../components/ui'
import { ApiError } from '../lib/api'
import { useAuth } from '../lib/auth'
import { errorMessage, formatDateTime, timeAgo } from '../lib/format'
import { useStaff, useTicket, useTicketAction, type TicketAction } from '../lib/queries'
import { availableActions } from '../lib/tickets'

export function TicketDetailPage() {
  const { id = '' } = useParams()
  const { user, hasRole } = useAuth()
  const { notify } = useToast()
  const ticket = useTicket(id)
  const action = useTicketAction(id)
  const isStaff = hasRole('Admin', 'Agent')
  const staff = useStaff(isStaff)
  const [assignee, setAssignee] = useState('')

  async function run(a: TicketAction, success: string) {
    try {
      await action.mutateAsync(a)
      notify('success', success)
    } catch (e) {
      // 400 traz a regra de negócio violada em português (ex.: "Responsável inválido...").
      notify('error', e instanceof ApiError ? e.message : errorMessage(e))
    }
  }

  if (ticket.isPending) return <LoadingBlock label="Carregando chamado…" />

  if (ticket.isError) {
    const notFound = ticket.error instanceof ApiError && ticket.error.status === 404
    return (
      <Card>
        <ErrorBlock message={notFound ? 'Chamado não encontrado (ou você não tem acesso a ele).' : errorMessage(ticket.error)} onRetry={notFound ? undefined : () => void ticket.refetch()} />
        <p className="pb-6 text-center">
          <Link to="/tickets" className="text-sm font-medium text-brand-600">
            ← Voltar para os chamados
          </Link>
        </p>
      </Card>
    )
  }

  const t = ticket.data
  const can = availableActions(t, isStaff)
  const busy = action.isPending

  return (
    <>
      <Link to="/tickets" className="mb-4 inline-block text-sm font-medium text-slate-500 hover:text-slate-800">
        ← Chamados
      </Link>

      <Card className="mb-6">
        <div className="border-b border-slate-200 p-6">
          <div className="flex flex-wrap items-start justify-between gap-3">
            <h1 className="min-w-0 flex-1 text-xl font-semibold text-slate-900">{t.title}</h1>
            <div className="flex items-center gap-2">
              <PriorityBadge priority={t.priority} />
              <StatusBadge status={t.status} />
            </div>
          </div>
          {t.slaBreachedAt && (
            <div className="mt-4">
              <Alert tone="amber">O prazo de primeiro atendimento estourou em {formatDateTime(t.slaBreachedAt)}. Os administradores foram avisados.</Alert>
            </div>
          )}
        </div>

        <div className="p-6">
          <h2 className="text-xs font-semibold uppercase tracking-wide text-slate-500">Descrição</h2>
          {/* O conteúdo vem de usuários: é exibido como TEXTO (React escapa), nunca como HTML. */}
          <p className="mt-2 whitespace-pre-wrap text-sm leading-relaxed text-slate-700">{t.description || 'Sem descrição.'}</p>
        </div>

        <dl className="grid gap-4 border-t border-slate-200 p-6 text-sm sm:grid-cols-2">
          <Info label="Solicitante" value={t.requesterId === user?.id ? 'Você' : (t.requesterName ?? 'Usuário')} />
          <Info label="Responsável" value={t.assigneeName ?? 'Ninguém assumiu ainda'} />
          <Info label="Aberto" value={`${formatDateTime(t.createdAt)} (${timeAgo(t.createdAt)})`} />
          {t.closedAt && <Info label="Fechado" value={formatDateTime(t.closedAt)} />}
        </dl>
      </Card>

      {user && <CommentThread ticket={t} isStaff={isStaff} currentUserId={user.id} />}

      {(can.assign || can.resolve || can.close || can.reopen) && (
        <Card className="p-6">
          <h2 className="mb-4 text-sm font-semibold text-slate-900">Ações</h2>

          {can.assign && (
            <div className="mb-5 flex flex-wrap items-end gap-3">
              {user && (
                <Button variant="secondary" disabled={busy || t.assigneeId === user.id} onClick={() => run({ assignTo: user.id }, 'Você assumiu o chamado.')}>
                  Assumir chamado
                </Button>
              )}
              <div className="min-w-48 flex-1 sm:max-w-xs">
                <Select label="Ou atribuir a" value={assignee} onChange={(e) => setAssignee(e.target.value)} disabled={staff.isPending}>
                  <option value="">Escolha alguém da equipe…</option>
                  {staff.data?.map((m) => (
                    <option key={m.id} value={m.id}>
                      {m.name}
                    </option>
                  ))}
                </Select>
              </div>
              <Button disabled={busy || !assignee} onClick={() => run({ assignTo: assignee }, 'Responsável atualizado.')}>
                Atribuir
              </Button>
            </div>
          )}

          <div className="flex flex-wrap gap-2">
            {can.resolve && (
              <Button loading={busy} onClick={() => run('resolve', 'Chamado marcado como resolvido. O solicitante foi avisado.')}>
                Marcar como resolvido
              </Button>
            )}
            {can.close && (
              <Button variant="secondary" loading={busy} onClick={() => run('close', 'Chamado fechado.')}>
                Fechar chamado
              </Button>
            )}
            {can.reopen && (
              <Button variant="secondary" loading={busy} onClick={() => run('reopen', 'Chamado reaberto.')}>
                Reabrir
              </Button>
            )}
          </div>
        </Card>
      )}
    </>
  )
}

function Info({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-xs font-semibold uppercase tracking-wide text-slate-500">{label}</dt>
      <dd className="mt-1 text-slate-800">{value}</dd>
    </div>
  )
}
