import { describe, expect, it } from 'vitest'
import { planUsage, usersLabel } from './plan'

describe('planUsage', () => {
  it('calcula o que sobra e a porcentagem', () => {
    expect(planUsage(2, 5)).toMatchObject({ used: 2, limit: 5, remaining: 3, percent: 40, isFull: false, level: 'ok' })
  })

  it('avisa a partir de 80% de uso', () => {
    expect(planUsage(4, 5)).toMatchObject({ percent: 80, level: 'warning', isFull: false })
  })

  it('marca como lotado exatamente no limite', () => {
    expect(planUsage(5, 5)).toMatchObject({ remaining: 0, percent: 100, isFull: true, level: 'full' })
  })

  it('nunca passa de 100% nem fica com "restante" negativo, mesmo com dados inconsistentes', () => {
    expect(planUsage(7, 5)).toMatchObject({ remaining: 0, percent: 100, isFull: true })
  })

  it('plano sem vagas (limite 0) já nasce lotado', () => {
    expect(planUsage(0, 0)).toMatchObject({ isFull: true, level: 'full' })
  })
})

describe('usersLabel', () => {
  it('usa singular e plural', () => {
    expect(usersLabel(1)).toBe('1 usuário')
    expect(usersLabel(5)).toBe('5 usuários')
  })
})
