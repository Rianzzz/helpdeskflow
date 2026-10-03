import { useQueryClient } from '@tanstack/react-query'
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { api, clearSession, hasStoredRefreshToken, refreshSession, storeSession, subscribeToSession } from './api'
import type { AuthResponse, Role, User } from './types'

interface AuthContextValue {
  user: User | null
  /** true enquanto tentamos restaurar a sessão a partir do refresh token guardado. */
  restoring: boolean
  login: (email: string, password: string) => Promise<User>
  logout: () => Promise<void>
  hasRole: (...roles: Role[]) => boolean
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<User | null>(null)
  const [restoring, setRestoring] = useState(hasStoredRefreshToken())
  const queryClient = useQueryClient()

  // Mantém o estado do React em sincronia com a sessão do cliente de API (login, renovação, expiração).
  useEffect(() => subscribeToSession(setUser), [])

  // Ao abrir (ou recarregar) a página: se há refresh token, tenta recuperar a sessão.
  useEffect(() => {
    if (!hasStoredRefreshToken()) return
    refreshSession().finally(() => setRestoring(false))
  }, [])

  // Quando alguém SAI (logout ou sessão revogada), descarta os dados em cache dessa pessoa. Só na transição
  // "logado -> deslogado": limpar também na montagem derrubaria consultas em andamento de telas públicas
  // (ex.: o acompanhamento do cadastro, que roda sem usuário).
  const previousUser = useRef<User | null>(null)
  useEffect(() => {
    if (previousUser.current !== null && user === null) queryClient.clear()
    previousUser.current = user
  }, [user, queryClient])

  const login = useCallback(async (email: string, password: string) => {
    const auth = await api<AuthResponse>('/api/auth/login', { method: 'POST', body: { email, password }, auth: false })
    storeSession(auth)
    return auth.user
  }, [])

  const logout = useCallback(async () => {
    const refreshToken = sessionStorage.getItem('hdf.refresh')
    try {
      // Revoga o refresh token no servidor; mesmo se falhar, encerramos a sessão local.
      if (refreshToken) await api('/api/auth/logout', { method: 'POST', body: { refreshToken }, auth: false })
    } catch {
      /* sem rede: o token expira sozinho */
    }
    clearSession()
  }, [])

  const value = useMemo<AuthContextValue>(
    () => ({
      user,
      restoring,
      login,
      logout,
      hasRole: (...roles) => user !== null && roles.includes(user.role),
    }),
    [user, restoring, login, logout],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

// eslint-disable-next-line react-refresh/only-export-components
export function useAuth() {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth precisa estar dentro de <AuthProvider>')
  return ctx
}
