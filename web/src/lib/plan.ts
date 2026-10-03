/** Quanto do limite de usuários do plano já foi usado. Funções puras: fáceis de testar. */

export interface PlanUsage {
  used: number
  limit: number
  remaining: number
  percent: number
  isFull: boolean
  /** "ok" até 79%, "warning" a partir de 80%, "full" quando acabou. */
  level: 'ok' | 'warning' | 'full'
}

export function planUsage(used: number, limit: number): PlanUsage {
  const safeLimit = Math.max(limit, 0)
  const remaining = Math.max(safeLimit - used, 0)
  const percent = safeLimit === 0 ? 100 : Math.min(Math.round((used / safeLimit) * 100), 100)
  const isFull = used >= safeLimit
  return { used, limit: safeLimit, remaining, percent, isFull, level: isFull ? 'full' : percent >= 80 ? 'warning' : 'ok' }
}

export const usersLabel = (n: number) => (n === 1 ? '1 usuário' : `${n} usuários`)
