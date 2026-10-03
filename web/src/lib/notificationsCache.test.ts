import { describe, expect, it } from 'vitest'
import { addReceived, addReceivedUnread, markAsRead, removeById } from './notificationsCache'
import type { AppNotification } from './types'

const make = (id: string, readAt: string | null = null): AppNotification => ({
  id,
  subject: `Aviso ${id}`,
  body: 'corpo',
  createdAt: '2026-10-04T10:00:00Z',
  readAt,
})

describe('addReceived', () => {
  it('coloca o aviso novo no topo da lista', () => {
    expect(addReceived([make('1')], make('2'))?.map((n) => n.id)).toEqual(['2', '1'])
  })

  it('ignora um aviso que já está na lista (o mesmo pode chegar por empurrão e por consulta)', () => {
    const list = [make('1')]

    expect(addReceived(list, make('1'))).toBe(list)
  })

  it('não inventa uma lista parcial quando a tela ainda nem buscou a lista', () => {
    expect(addReceived(undefined, make('1'))).toBeUndefined()
  })

  it('mantém o limite de 50 itens, descartando os mais antigos', () => {
    const full = Array.from({ length: 50 }, (_, i) => make(`n${i}`))

    const result = addReceived(full, make('novo'))!

    expect(result).toHaveLength(50)
    expect(result[0].id).toBe('novo')
    expect(result.at(-1)?.id).toBe('n48')
  })

  it('não altera a lista recebida', () => {
    const list = [make('1')]
    addReceived(list, make('2'))

    expect(list).toHaveLength(1)
  })
})

describe('addReceivedUnread', () => {
  it('só considera avisos realmente não lidos', () => {
    expect(addReceivedUnread([], make('1', '2026-10-04T11:00:00Z'))).toEqual([])
    expect(addReceivedUnread([], make('2'))).toHaveLength(1)
  })
})

describe('markAsRead / removeById', () => {
  it('marca só o aviso indicado como lido e preserva o horário de quem já estava lido', () => {
    const list = [make('1'), make('2', '2026-10-01T00:00:00Z')]

    const result = markAsRead(list, '1', '2026-10-04T12:00:00Z')!

    expect(result[0].readAt).toBe('2026-10-04T12:00:00Z')
    expect(markAsRead(list, '2', '2026-10-04T12:00:00Z')![1].readAt).toBe('2026-10-01T00:00:00Z')
  })

  it('remove da lista de não lidas', () => {
    expect(removeById([make('1'), make('2')], '1')?.map((n) => n.id)).toEqual(['2'])
  })

  it('aceitam "sem cache" sem quebrar', () => {
    expect(markAsRead(undefined, '1', 'x')).toBeUndefined()
    expect(removeById(undefined, '1')).toBeUndefined()
  })
})
