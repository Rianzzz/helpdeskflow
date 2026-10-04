import type { ReactNode } from 'react'

/** Moldura das telas de visitante (login, cadastro, status do cadastro). */
export function AuthShell({ title, subtitle, children }: { title: string; subtitle?: string; children: ReactNode }) {
  return (
    <div className="flex min-h-full items-center justify-center bg-slate-50 px-4 py-10">
      <div className="w-full max-w-sm">
        <div className="mb-6 flex items-center gap-2">
          {/* A mesma marca do menu: três barras que encurtam, como uma fila sendo atendida. */}
          <svg viewBox="0 0 24 24" className="h-6 w-6" aria-hidden="true">
            <rect width="24" height="24" rx="5" className="fill-brand-600" />
            <path d="M6 8h12M6 12h8.5M6 16h5" stroke="white" strokeWidth="2" strokeLinecap="square" fill="none" />
          </svg>
          <span className="text-base font-semibold tracking-tight text-slate-900">HelpDeskFlow</span>
        </div>
        <div className="rounded-md border border-slate-200 bg-white p-6">
          <h1 className="text-lg font-semibold text-slate-900">{title}</h1>
          {subtitle && <p className="mt-1 text-[13px] text-slate-500">{subtitle}</p>}
          <div className="mt-5">{children}</div>
        </div>
      </div>
    </div>
  )
}
