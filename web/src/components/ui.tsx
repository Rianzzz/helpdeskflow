import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type ButtonHTMLAttributes,
  type InputHTMLAttributes,
  type ReactNode,
  type SelectHTMLAttributes,
  type TextareaHTMLAttributes,
} from 'react'
import { priorityLabel, statusLabel } from '../lib/format'
import type { TicketPriority, TicketStatus } from '../lib/types'

const cx = (...parts: (string | false | null | undefined)[]) => parts.filter(Boolean).join(' ')

// ───────────── Botões ─────────────

type ButtonVariant = 'primary' | 'secondary' | 'danger' | 'ghost'

const buttonStyles: Record<ButtonVariant, string> = {
  primary: 'border-transparent bg-brand-600 text-white hover:bg-brand-700 disabled:bg-brand-600/50',
  secondary: 'border-slate-300 bg-white text-slate-700 hover:bg-slate-50 disabled:text-slate-400',
  danger: 'border-transparent bg-red-600 text-white hover:bg-red-700 disabled:bg-red-600/50',
  ghost: 'border-transparent text-slate-600 hover:bg-slate-100 disabled:text-slate-400',
}

export function Button({
  variant = 'primary',
  loading = false,
  className,
  children,
  disabled,
  ...rest
}: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant; loading?: boolean }) {
  return (
    <button
      {...rest}
      disabled={disabled || loading}
      className={cx(
        'inline-flex items-center justify-center gap-2 rounded-md border px-3 py-1.5 text-[13px] font-medium transition-colors disabled:cursor-not-allowed',
        buttonStyles[variant],
        className,
      )}
    >
      {loading && <Spinner className="h-4 w-4" />}
      {children}
    </button>
  )
}

export function Spinner({ className = 'h-5 w-5' }: { className?: string }) {
  return (
    <svg className={cx('animate-spin', className)} viewBox="0 0 24 24" fill="none" role="status" aria-label="Carregando">
      <circle cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" className="opacity-20" />
      <path d="M22 12a10 10 0 0 0-10-10" stroke="currentColor" strokeWidth="4" strokeLinecap="round" />
    </svg>
  )
}

// ───────────── Campos de formulário ─────────────

interface FieldShellProps {
  label: string
  hint?: string
  error?: string | null
  children: (props: { id: string; 'aria-invalid': boolean; 'aria-describedby': string | undefined }) => ReactNode
}

function FieldShell({ label, hint, error, children }: FieldShellProps) {
  const id = useId()
  const helpId = `${id}-help`
  const message = error ?? hint

  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-[13px] font-medium text-slate-700">
        {label}
      </label>
      {children({ id, 'aria-invalid': Boolean(error), 'aria-describedby': message ? helpId : undefined })}
      {message && (
        <p id={helpId} className={cx('text-xs', error ? 'text-red-600' : 'text-slate-500')}>
          {message}
        </p>
      )}
    </div>
  )
}

const fieldStyles =
  'block w-full rounded-md border-0 bg-white px-2.5 py-1.5 text-sm text-slate-900 ring-1 ring-inset ring-slate-300 placeholder:text-slate-500 focus:ring-2 focus:ring-brand-600 aria-[invalid=true]:ring-red-500'

export function Input({
  label,
  hint,
  error,
  ...rest
}: InputHTMLAttributes<HTMLInputElement> & { label: string; hint?: string; error?: string | null }) {
  return (
    <FieldShell label={label} hint={hint} error={error}>
      {(a11y) => <input {...a11y} {...rest} className={fieldStyles} />}
    </FieldShell>
  )
}

export function Textarea({
  label,
  hint,
  error,
  ...rest
}: TextareaHTMLAttributes<HTMLTextAreaElement> & { label: string; hint?: string; error?: string | null }) {
  return (
    <FieldShell label={label} hint={hint} error={error}>
      {(a11y) => <textarea {...a11y} rows={4} {...rest} className={fieldStyles} />}
    </FieldShell>
  )
}

export function Select({
  label,
  hint,
  error,
  children,
  ...rest
}: SelectHTMLAttributes<HTMLSelectElement> & { label: string; hint?: string; error?: string | null }) {
  return (
    <FieldShell label={label} hint={hint} error={error}>
      {(a11y) => (
        <select {...a11y} {...rest} className={fieldStyles}>
          {children}
        </select>
      )}
    </FieldShell>
  )
}

// ───────────── Cartões, selos, estados ─────────────

export function Card({ className, children }: { className?: string; children: ReactNode }) {
  return <div className={cx('rounded-md border border-slate-200 bg-white', className)}>{children}</div>
}

// Rótulo pequeno e chapado (papel do usuário, "Nota interna"...). Sem anel, sem pílula.
const badgeTones = {
  slate: 'bg-slate-100 text-slate-700',
  amber: 'bg-amber-100 text-amber-900',
  red: 'bg-red-50 text-red-700',
  brand: 'bg-brand-50 text-brand-700',
}

export function Badge({ tone = 'slate', children }: { tone?: keyof typeof badgeTones; children: ReactNode }) {
  return <span className={cx('inline-flex items-center rounded-sm px-1.5 py-0.5 text-[11px] font-medium', badgeTones[tone])}>{children}</span>
}

// ───────────── Estado e prioridade do chamado: ícone + texto ─────────────
// O estado é lido pelo formato do ícone (círculo vazio, meio cheio, com visto, com x) e só em segundo plano pela cor.
// O texto fica sempre ao lado: a cor nunca é a única informação.

const Icon = ({ children, className }: { children: ReactNode; className?: string }) => (
  <svg viewBox="0 0 16 16" className={cx('h-3.5 w-3.5 shrink-0', className)} aria-hidden="true">
    {children}
  </svg>
)

const statusIcon: Record<TicketStatus, ReactNode> = {
  Open: (
    <Icon className="text-brand-600">
      <circle cx="8" cy="8" r="6" fill="none" stroke="currentColor" strokeWidth="1.5" />
    </Icon>
  ),
  InProgress: (
    <Icon className="text-amber-600">
      <circle cx="8" cy="8" r="6" fill="none" stroke="currentColor" strokeWidth="1.5" />
      <path d="M8 2a6 6 0 0 1 0 12z" fill="currentColor" />
    </Icon>
  ),
  Resolved: (
    <Icon className="text-emerald-600">
      <circle cx="8" cy="8" r="7" fill="currentColor" />
      <path d="M5 8.2l2.1 2.1L11 6.2" fill="none" stroke="white" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
    </Icon>
  ),
  Closed: (
    <Icon className="text-slate-400">
      <circle cx="8" cy="8" r="7" fill="currentColor" />
      <path d="M5.5 5.5l5 5m0-5l-5 5" stroke="white" strokeWidth="1.5" strokeLinecap="round" />
    </Icon>
  ),
}

export const StatusBadge = ({ status }: { status: TicketStatus }) => (
  <span className="inline-flex items-center gap-1.5 whitespace-nowrap text-[13px] text-slate-700">
    {statusIcon[status]}
    {statusLabel[status]}
  </span>
)

/** Barras de sinal (1 a 3) para baixa, média e alta; um quadrado vermelho com "!" para urgente. */
function PriorityIcon({ priority }: { priority: TicketPriority }) {
  if (priority === 'Urgent') {
    return (
      <Icon className="text-red-600">
        <rect x="1.5" y="1.5" width="13" height="13" rx="2.5" fill="currentColor" />
        <path d="M8 4.5v4.2M8 11v.1" stroke="white" strokeWidth="1.7" strokeLinecap="round" />
      </Icon>
    )
  }
  const level = { Low: 1, Medium: 2, High: 3 }[priority]
  const color = priority === 'High' ? 'text-orange-600' : 'text-slate-500'
  return (
    <Icon className={color}>
      {[0, 1, 2].map((i) => (
        <rect key={i} x={2 + i * 4.5} y={10 - i * 3} width="3" height={4 + i * 3} rx="0.8" fill="currentColor" opacity={i < level ? 1 : 0.22} />
      ))}
    </Icon>
  )
}

export const PriorityBadge = ({ priority, className }: { priority: TicketPriority; className?: string }) => (
  <span className={cx('inline-flex items-center gap-1.5 whitespace-nowrap text-[13px] text-slate-700', className)}>
    <PriorityIcon priority={priority} />
    {priorityLabel[priority]}
  </span>
)

export function PageHeader({ title, subtitle, actions }: { title: string; subtitle?: string; actions?: ReactNode }) {
  return (
    <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
      <div>
        <h1 className="text-lg font-semibold tracking-tight text-slate-900">{title}</h1>
        {subtitle && <p className="mt-0.5 text-[13px] text-slate-500">{subtitle}</p>}
      </div>
      {actions && <div className="flex items-center gap-2">{actions}</div>}
    </div>
  )
}

export function EmptyState({ title, description, action }: { title: string; description?: string; action?: ReactNode }) {
  return (
    <div className="px-6 py-12 text-center">
      <p className="text-sm font-medium text-slate-900">{title}</p>
      {description && <p className="mx-auto mt-1 max-w-sm text-sm text-slate-500">{description}</p>}
      {action && <div className="mt-4">{action}</div>}
    </div>
  )
}

export function LoadingBlock({ label = 'Carregando…' }: { label?: string }) {
  return (
    <div className="flex items-center justify-center gap-3 px-6 py-14 text-sm text-slate-500">
      <Spinner />
      {label}
    </div>
  )
}

export function ErrorBlock({ message, onRetry }: { message: string; onRetry?: () => void }) {
  return (
    <div role="alert" className="px-6 py-10 text-center">
      <p className="text-sm font-medium text-red-700">{message}</p>
      {onRetry && (
        <Button variant="secondary" className="mt-3" onClick={onRetry}>
          Tentar novamente
        </Button>
      )}
    </div>
  )
}

export function Alert({ tone = 'red', children }: { tone?: 'red' | 'green' | 'amber'; children: ReactNode }) {
  const tones = {
    red: 'bg-red-50 text-red-800 ring-red-200',
    green: 'bg-emerald-50 text-emerald-800 ring-emerald-200',
    amber: 'bg-amber-50 text-amber-900 ring-amber-200',
  }
  return (
    <div role={tone === 'red' ? 'alert' : 'status'} className={cx('rounded-md px-3 py-2.5 text-[13px] ring-1 ring-inset', tones[tone])}>
      {children}
    </div>
  )
}

// ───────────── Modal (usa <dialog>: foco preso e Esc fechando, de graça e acessível) ─────────────

export function Modal({ open, onClose, title, children }: { open: boolean; onClose: () => void; title: string; children: ReactNode }) {
  const ref = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    if (open && !dialog.open) dialog.showModal()
    if (!open && dialog.open) dialog.close()
  }, [open])

  return (
    <dialog
      ref={ref}
      onClose={onClose}
      onClick={(e) => e.target === ref.current && onClose()} // clicar no fundo escuro fecha
      aria-label={title}
      className="m-auto w-[calc(100%-2rem)] max-w-lg rounded-lg border border-slate-200 p-0 shadow-lg backdrop:bg-slate-900/40"
    >
      {open && (
        <div className="p-5">
          <h2 className="mb-4 text-base font-semibold text-slate-900">{title}</h2>
          {children}
        </div>
      )}
    </dialog>
  )
}

// ───────────── Avisos (toasts) ─────────────

interface Toast {
  id: number
  tone: 'success' | 'error'
  message: string
}

const ToastContext = createContext<{ notify: (tone: Toast['tone'], message: string) => void } | null>(null)

export function ToastProvider({ children }: { children: ReactNode }) {
  const [toasts, setToasts] = useState<Toast[]>([])
  const nextId = useRef(1)

  const notify = useCallback((tone: Toast['tone'], message: string) => {
    const id = nextId.current++
    setToasts((current) => [...current, { id, tone, message }])
    setTimeout(() => setToasts((current) => current.filter((t) => t.id !== id)), 5000)
  }, [])

  const value = useMemo(() => ({ notify }), [notify])

  return (
    <ToastContext.Provider value={value}>
      {children}
      <div aria-live="polite" className="pointer-events-none fixed inset-x-0 bottom-4 z-50 flex flex-col items-center gap-2 px-4">
        {toasts.map((t) => (
          <div
            key={t.id}
            role={t.tone === 'error' ? 'alert' : 'status'}
            className={cx(
              'pointer-events-auto max-w-md rounded-md px-3.5 py-2.5 text-[13px] font-medium text-white shadow-lg',
              t.tone === 'error' ? 'bg-red-600' : 'bg-slate-900',
            )}
          >
            {t.message}
          </div>
        ))}
      </div>
    </ToastContext.Provider>
  )
}

// eslint-disable-next-line react-refresh/only-export-components
export function useToast() {
  const ctx = useContext(ToastContext)
  if (!ctx) throw new Error('useToast precisa estar dentro de <ToastProvider>')
  return ctx
}
