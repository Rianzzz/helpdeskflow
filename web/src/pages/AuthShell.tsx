import type { ReactNode } from 'react'

/** Moldura das telas de visitante (login, cadastro, status do cadastro). */
export function AuthShell({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <div className="flex min-h-full items-center justify-center bg-gradient-to-br from-brand-50 via-white to-slate-100 px-4 py-10">
      <div className="w-full max-w-md">
        <div className="mb-8 flex items-center justify-center gap-2.5">
          <span className="flex h-10 w-10 items-center justify-center rounded-xl bg-brand-600 text-white shadow-sm">
            <svg viewBox="0 0 24 24" fill="currentColor" className="h-6 w-6" aria-hidden="true">
              <path d="M4.5 8.25A3.75 3.75 0 0 1 8.25 4.5h7.5a3.75 3.75 0 0 1 3.75 3.75v4.5a3.75 3.75 0 0 1-3.75 3.75h-3.4l-4.1 3v-3.04A3.75 3.75 0 0 1 4.5 12.75z" />
            </svg>
          </span>
          <span className="text-2xl font-semibold tracking-tight text-slate-900">HelpDeskFlow</span>
        </div>
        <div className="rounded-2xl bg-white p-8 shadow-xl shadow-slate-200/60 ring-1 ring-slate-200">
          <h1 className="text-xl font-semibold text-slate-900">{title}</h1>
          {subtitle && <p className="mt-1 text-sm text-slate-500">{subtitle}</p>}
          <div className="mt-6">{children}</div>
        </div>
      </div>
    </div>
  )
}
