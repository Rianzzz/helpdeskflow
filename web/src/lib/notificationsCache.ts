import type { AppNotification } from './types'

/**
 * Funções puras que atualizam as listas de notificações guardadas no cache quando chega algo em tempo real.
 * Ficam separadas do SignalR para serem fáceis de testar. Cada uma devolve uma lista NOVA (nunca altera a recebida)
 * e deixa "sem cache" (undefined) como está: se a tela ainda nem buscou a lista, não inventamos uma lista parcial.
 */

const MAX_ITEMS = 50 // o servidor devolve no máximo 50; mantemos o mesmo limite

export function addReceived(list: AppNotification[] | undefined, received: AppNotification): AppNotification[] | undefined {
  if (!list) return list
  if (list.some((n) => n.id === received.id)) return list // o mesmo aviso pode chegar duas vezes (empurrão + consulta)
  return [received, ...list].slice(0, MAX_ITEMS)
}

/** Lista de NÃO LIDAS: só entram as realmente não lidas. */
export function addReceivedUnread(list: AppNotification[] | undefined, received: AppNotification): AppNotification[] | undefined {
  return received.readAt === null ? addReceived(list, received) : list
}

export function markAsRead(list: AppNotification[] | undefined, id: string, readAt: string): AppNotification[] | undefined {
  return list?.map((n) => (n.id === id && n.readAt === null ? { ...n, readAt } : n))
}

export function removeById(list: AppNotification[] | undefined, id: string): AppNotification[] | undefined {
  return list?.filter((n) => n.id !== id)
}
