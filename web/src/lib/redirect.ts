/**
 * Destino seguro para depois do login. Só aceita caminhos INTERNOS da aplicação: ignora qualquer coisa que pudesse
 * levar para outro site (ex.: "https://evil.com" ou "//evil.com") e não volta para as próprias telas de visitante.
 */
export function safeRedirectTarget(from: string | undefined | null, fallback = '/tickets'): string {
  if (!from || !from.startsWith('/') || from.startsWith('//') || from.includes('\\')) return fallback
  if (from === '/' || from.startsWith('/login') || from.startsWith('/register')) return fallback
  return from
}
