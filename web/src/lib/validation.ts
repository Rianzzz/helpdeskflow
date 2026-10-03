/** Mesmas regras de senha do servidor (que SEMPRE revalida): aqui serve só para dar retorno imediato. */
export function passwordProblem(password: string): string | null {
  if (password.length < 10) return 'Use pelo menos 10 caracteres.'
  if (password.length > 128) return 'Use no máximo 128 caracteres.'
  if (!/\p{L}/u.test(password) || !/\d/.test(password)) return 'Misture letras e números.'
  return null
}
