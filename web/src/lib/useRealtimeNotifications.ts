import { useQueryClient } from '@tanstack/react-query'
import { useEffect } from 'react'
import { useToast } from '../components/ui'
import { useAuth } from './auth'
import { addReceived, addReceivedUnread, markAsRead, removeById } from './notificationsCache'
import { keys } from './queries'
import { startRealtime } from './realtime'
import type { AppNotification } from './types'

/**
 * Liga o tempo real enquanto há alguém logado e mantém o cache de dados em dia com o que o servidor empurra:
 *  • aviso novo  → entra na lista, o contador do menu sobe e aparece um aviso rápido na tela;
 *  • lido em outra aba/dispositivo → o contador desce aqui também;
 *  • (re)conexão → recarrega as listas, para pegar o que passou enquanto estávamos desconectados.
 */
export function useRealtimeNotifications() {
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const { notify } = useToast()
  const userId = user?.id

  useEffect(() => {
    if (!userId) return

    const realtime = startRealtime({
      onReceived: (notification: AppNotification) => {
        queryClient.setQueryData<AppNotification[]>(keys.notifications, (list) => addReceived(list, notification))
        queryClient.setQueryData<AppNotification[]>(keys.unread, (list) => addReceivedUnread(list, notification))
        // Um aviso costuma significar que um chamado mudou (novo, atribuído, resolvido, SLA): atualiza a lista também.
        void queryClient.invalidateQueries({ queryKey: keys.tickets })
        notify('success', notification.subject)
      },
      onRead: (id) => {
        const readAt = new Date().toISOString()
        queryClient.setQueryData<AppNotification[]>(keys.notifications, (list) => markAsRead(list, id, readAt))
        queryClient.setQueryData<AppNotification[]>(keys.unread, (list) => removeById(list, id))
      },
      onResync: () => {
        void queryClient.invalidateQueries({ queryKey: keys.notifications })
        void queryClient.invalidateQueries({ queryKey: keys.tickets })
      },
    })

    return () => realtime.stop()
  }, [userId, queryClient, notify])
}
