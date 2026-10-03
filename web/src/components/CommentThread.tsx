import { useState, type FormEvent, type KeyboardEvent } from 'react'
import { ApiError } from '../lib/api'
import { errorMessage, formatDateTime, roleLabel, timeAgo } from '../lib/format'
import { useAddComment, useComments } from '../lib/queries'
import type { Ticket, TicketComment } from '../lib/types'
import { Badge, Button, Card, ErrorBlock, LoadingBlock, useToast } from './ui'

const MAX_LENGTH = 4000

interface CommentThreadProps {
  ticket: Ticket
  /** Admin ou Agent: só a equipe vê e cria notas internas. (A tela só facilita: o servidor decide.) */
  isStaff: boolean
  currentUserId: string
}

/** A conversa de um chamado: respostas públicas e, para a equipe, notas internas. */
export function CommentThread({ ticket, isStaff, currentUserId }: CommentThreadProps) {
  const comments = useComments(ticket.id)

  return (
    <Card className="mb-6">
      <div className="border-b border-slate-200 px-6 py-4">
        <h2 className="text-sm font-semibold text-slate-900">Conversa</h2>
        {isStaff && <p className="mt-0.5 text-xs text-slate-600">Notas internas aparecem em destaque e só a equipe consegue vê-las.</p>}
      </div>

      {comments.isPending ? (
        <LoadingBlock label="Carregando conversa…" />
      ) : comments.isError ? (
        <ErrorBlock message={errorMessage(comments.error, 'Não foi possível carregar a conversa.')} onRetry={() => void comments.refetch()} />
      ) : comments.data.length === 0 ? (
        <p className="px-6 py-8 text-center text-sm text-slate-600">Ainda não há mensagens. Escreva a primeira resposta abaixo.</p>
      ) : (
        <ol className="divide-y divide-slate-100" aria-label="Mensagens do chamado">
          {comments.data.map((c) => (
            <CommentItem key={c.id} comment={c} mine={c.authorId === currentUserId} />
          ))}
        </ol>
      )}

      <Composer ticket={ticket} isStaff={isStaff} />
    </Card>
  )
}

function CommentItem({ comment, mine }: { comment: TicketComment; mine: boolean }) {
  const name = mine ? 'Você' : (comment.authorName ?? 'Usuário')

  return (
    <li className={`flex gap-3 px-6 py-4 ${comment.isInternal ? 'bg-amber-50/70' : ''}`} data-internal={comment.isInternal}>
      <span
        aria-hidden="true"
        className="flex h-8 w-8 shrink-0 items-center justify-center rounded-full bg-brand-100 text-sm font-semibold text-brand-700"
      >
        {name.charAt(0).toUpperCase()}
      </span>
      <div className="min-w-0 flex-1">
        <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
          <span className="text-sm font-medium text-slate-900">{name}</span>
          {comment.authorRole && <Badge tone={comment.authorRole === 'Customer' ? 'slate' : 'brand'}>{roleLabel[comment.authorRole]}</Badge>}
          {comment.isInternal && <Badge tone="amber">Nota interna</Badge>}
          <time dateTime={comment.createdAt} title={formatDateTime(comment.createdAt)} className="text-xs text-slate-600">
            {timeAgo(comment.createdAt)}
          </time>
        </div>
        {/* O texto vem de usuários: é exibido como TEXTO (React escapa), nunca como HTML. */}
        <p className="mt-1 whitespace-pre-wrap break-words text-sm leading-relaxed text-slate-700">{comment.body}</p>
      </div>
    </li>
  )
}

function Composer({ ticket, isStaff }: { ticket: Ticket; isStaff: boolean }) {
  const add = useAddComment(ticket.id)
  const { notify } = useToast()
  const [body, setBody] = useState('')
  const [internal, setInternal] = useState(false)

  if (ticket.status === 'Closed') {
    return (
      <p className="border-t border-slate-200 bg-slate-50 px-6 py-4 text-sm text-slate-600">
        Este chamado está fechado. Reabra-o para continuar a conversa.
      </p>
    )
  }

  const trimmed = body.trim()

  async function submit() {
    if (!trimmed) return
    try {
      await add.mutateAsync({ body: trimmed, isInternal: isStaff && internal })
      setBody('')
      notify('success', internal ? 'Nota interna adicionada.' : 'Mensagem enviada.')
    } catch (e) {
      notify('error', e instanceof ApiError ? e.message : errorMessage(e))
    }
  }

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    void submit()
  }

  // Ctrl+Enter (ou ⌘+Enter) envia, como em qualquer ferramenta de conversa.
  function handleKeyDown(event: KeyboardEvent<HTMLTextAreaElement>) {
    if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
      event.preventDefault()
      void submit()
    }
  }

  const internalMode = isStaff && internal

  return (
    <form
      onSubmit={handleSubmit}
      className={`border-t px-6 py-4 ${internalMode ? 'border-amber-200 bg-amber-50/70' : 'border-slate-200'}`}
    >
      <label htmlFor="comment-body" className="mb-1.5 block text-sm font-medium text-slate-700">
        {internalMode ? 'Nota interna (invisível para o cliente)' : 'Sua mensagem'}
      </label>
      <textarea
        id="comment-body"
        rows={3}
        maxLength={MAX_LENGTH}
        value={body}
        onChange={(e) => setBody(e.target.value)}
        onKeyDown={handleKeyDown}
        placeholder={internalMode ? 'Escreva um recado para a equipe…' : 'Escreva uma resposta…'}
        className="block w-full rounded-lg border-0 bg-white px-3 py-2 text-sm text-slate-900 shadow-sm ring-1 ring-slate-300 placeholder:text-slate-500 focus:ring-2 focus:ring-brand-600"
      />

      <div className="mt-3 flex flex-wrap items-center justify-between gap-3">
        <div className="flex flex-wrap items-center gap-4">
          {isStaff && (
            <label className="flex items-center gap-2 text-sm text-slate-700">
              <input
                type="checkbox"
                checked={internal}
                onChange={(e) => setInternal(e.target.checked)}
                className="h-4 w-4 rounded border-slate-300 text-brand-600 focus:ring-brand-600"
              />
              Nota interna (só a equipe vê)
            </label>
          )}
          <span className="text-xs text-slate-600">
            {body.length}/{MAX_LENGTH} · Ctrl+Enter envia
          </span>
        </div>
        <Button type="submit" loading={add.isPending} disabled={!trimmed}>
          {internalMode ? 'Adicionar nota interna' : 'Enviar'}
        </Button>
      </div>
    </form>
  )
}
