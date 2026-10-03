import { describe, expect, it } from 'vitest'
import { safeRedirectTarget } from './redirect'

describe('safeRedirectTarget', () => {
  it('volta para a página interna que a pessoa queria abrir', () => {
    expect(safeRedirectTarget('/notifications')).toBe('/notifications')
    expect(safeRedirectTarget('/tickets/abc-123')).toBe('/tickets/abc-123')
  })

  it('usa os chamados quando não há destino', () => {
    expect(safeRedirectTarget(undefined)).toBe('/tickets')
    expect(safeRedirectTarget(null)).toBe('/tickets')
    expect(safeRedirectTarget('')).toBe('/tickets')
  })

  it.each(['https://evil.com', '//evil.com', 'javascript:alert(1)', '/\\evil.com', 'evil.com/tickets'])(
    'ignora destino que poderia levar para fora da aplicação: %s',
    (target) => {
      expect(safeRedirectTarget(target)).toBe('/tickets')
    },
  )

  it('não volta para as próprias telas de visitante (evita laço)', () => {
    expect(safeRedirectTarget('/login')).toBe('/tickets')
    expect(safeRedirectTarget('/register/status/x')).toBe('/tickets')
    expect(safeRedirectTarget('/')).toBe('/tickets')
  })
})
