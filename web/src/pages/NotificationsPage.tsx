import { Button, Card, EmptyState, ErrorBlock, LoadingBlock, PageHeader, useToast } from '../components/ui'
import { errorMessage, timeAgo } from '../lib/format'
import { useMarkRead, useNotifications } from '../lib/queries'

export function NotificationsPage() {
  const notifications = useNotifications()
  const markRead = useMarkRead()
  const { notify } = useToast()

  const unread = notifications.data?.filter((n) => n.readAt === null) ?? []

  async function markAllRead() {
    try {
      await Promise.all(unread.map((n) => markRead.mutateAsync(n.id)))
    } catch (e) {
      notify('error', errorMessage(e))
    }
  }

  return (
    <>
      <PageHeader
        title="Notificações"
        subtitle="Avisos sobre os seus chamados e a sua empresa."
        actions={
          unread.length > 0 ? (
            <Button variant="secondary" onClick={markAllRead} loading={markRead.isPending}>
              Marcar todas como lidas
            </Button>
          ) : undefined
        }
      />

      <Card>
        {notifications.isPending ? (
          <LoadingBlock label="Carregando notificações…" />
        ) : notifications.isError ? (
          <ErrorBlock message={errorMessage(notifications.error)} onRetry={() => void notifications.refetch()} />
        ) : notifications.data.length === 0 ? (
          <EmptyState title="Tudo em dia" description="Você ainda não recebeu nenhuma notificação." />
        ) : (
          <ul className="divide-y divide-slate-100">
            {notifications.data.map((n) => {
              const isUnread = n.readAt === null
              return (
                <li key={n.id} className={`flex items-start gap-3 px-5 py-4 ${isUnread ? 'bg-brand-50/50' : ''}`}>
                  <span aria-hidden="true" className={`mt-1.5 h-2 w-2 shrink-0 rounded-full ${isUnread ? 'bg-brand-600' : 'bg-transparent'}`} />
                  <div className="min-w-0 flex-1">
                    <p className={`text-sm ${isUnread ? 'font-semibold text-slate-900' : 'font-medium text-slate-700'}`}>{n.subject}</p>
                    <p className="mt-0.5 text-sm text-slate-600">{n.body}</p>
                    <p className="mt-1 text-xs text-slate-600">{timeAgo(n.createdAt)}</p>
                  </div>
                  {isUnread && (
                    <Button variant="ghost" className="shrink-0" onClick={() => markRead.mutate(n.id)} disabled={markRead.isPending}>
                      Marcar como lida
                    </Button>
                  )}
                </li>
              )
            })}
          </ul>
        )}
      </Card>
    </>
  )
}
